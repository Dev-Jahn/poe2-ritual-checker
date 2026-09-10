using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ritual.Core;

public static class J
{
    public static JsonNode? Get(this JsonNode? node, string key) =>
        node is JsonObject o
            ? o.FirstOrDefault(p =>
                string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)
            ).Value
            : null;

    public static string Text(this JsonNode? node, string key) => node.Get(key)?.ToString() ?? "";

    public static decimal? Number(this JsonNode? node, string key) =>
        decimal.TryParse(
            node.Text(key),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var n
        )
            ? n
            : null;

    public static IEnumerable<JsonNode> Rows(this JsonNode? node) =>
        node is JsonArray a ? a.Where(n => n is not null).Select(n => n!) : [];
}

public sealed class MarketCooldownException(DateTimeOffset retryAt)
    : InvalidOperationException(
        $"요청 대기 · {Math.Max(1, Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds))}초 후 자동 재개"
    )
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}

public sealed class MarketHttp : IDisposable
{
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(1);
    private DateTimeOffset next = DateTimeOffset.MinValue;
    private bool blocked;
    private readonly double spacingSeconds;
    private readonly string? cooldownPath;
    private readonly Dictionary<string, (DateTimeOffset At, string Json)> responses = [];

    public MarketHttp(string host, HttpMessageHandler? handler = null)
    {
        client = handler is null
            ? new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            : new(handler);
        spacingSeconds = new Uri(host).Host.EndsWith("pathofexile.com") ? 3 : 1;
        if (handler is null)
        {
            cooldownPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RitualChecker",
                "network",
                new Uri(host).Host + ".json"
            );
            try
            {
                next = JsonFiles.Read<DateTimeOffset>(cooldownPath);
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        }
        client.BaseAddress = new Uri(host);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RitualChecker/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<JsonNode> RequestAsync(
        string path,
        CancellationToken token,
        JsonNode? body = null
    )
    {
        await gate.WaitAsync(token);
        try
        {
            string responseKey = path + "|" + body?.ToJsonString();
            if (
                responses.TryGetValue(responseKey, out var cachedResponse)
                && DateTimeOffset.UtcNow - cachedResponse.At < TimeSpan.FromMinutes(5)
            )
                return JsonNode.Parse(cachedResponse.Json)!;
            if (blocked)
                throw new InvalidOperationException(
                    "거래소 인증/접근 확인 필요 · 앱을 다시 시작한 뒤 재시도하세요."
                );
            var delay = next - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.FromSeconds(5))
                throw new MarketCooldownException(next);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token);
            using var request = new HttpRequestMessage(
                body is null ? HttpMethod.Get : HttpMethod.Post,
                path
            );
            if (body is not null)
                request.Content = JsonContent.Create(body);
            next = DateTimeOffset.UtcNow.AddSeconds(spacingSeconds);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token
            );
            Observe(response);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                blocked = true;
                throw new InvalidOperationException("거래소 인증 또는 접근 확인 필요");
            }
            if ((int)response.StatusCode == 429)
            {
                // Use the server's actual deadline; 60 seconds is only a fallback
                // for a response that supplies no usable retry information.
                if (!HasRetryDeadline(response))
                    next = Max(next, DateTimeOffset.UtcNow.AddSeconds(60));
                PersistCooldown();
                throw new MarketCooldownException(next);
            }
            response.EnsureSuccessStatusCode();
            if (!((response.Content.Headers.ContentType?.MediaType ?? "").Contains("json")))
                throw new InvalidDataException("시세 서버가 JSON 대신 다른 내용을 반환했습니다.");
            if (response.Content.Headers.ContentLength > 8 * 1024 * 1024)
                throw new InvalidDataException("시세 응답 크기 초과");
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(bytes, token)) > 0)
            {
                if (buffer.Length + count > 8 * 1024 * 1024)
                    throw new InvalidDataException("시세 응답 크기 초과");
                buffer.Write(bytes, 0, count);
            }
            var data =
                JsonNode.Parse(buffer.ToArray()) ?? throw new InvalidDataException("빈 시세 응답");
            if (data.Get("error") is not null)
                throw new InvalidDataException("시세 API 오류: " + data.Get("error"));
            responses[responseKey] = (DateTimeOffset.UtcNow, data.ToJsonString());
            foreach (
                var key in responses
                    .Where(p => DateTimeOffset.UtcNow - p.Value.At > TimeSpan.FromMinutes(5))
                    .Select(p => p.Key)
                    .ToArray()
            )
                responses.Remove(key);
            return data;
        }
        finally
        {
            gate.Release();
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static bool HasRetryDeadline(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { TotalSeconds: > 0 }
        || response.Headers.RetryAfter?.Date > DateTimeOffset.UtcNow
        || response
            .Headers.Where(h =>
                h.Key.StartsWith("X-Rate-Limit-", StringComparison.OrdinalIgnoreCase)
                && h.Key.EndsWith("-State", StringComparison.OrdinalIgnoreCase)
            )
            .SelectMany(h => h.Value)
            .SelectMany(v => v.Split(','))
            .Any(v =>
                v.Split(':') is [_, _, var blocked]
                && int.TryParse(blocked, out var seconds)
                && seconds > 0
            );

    internal static DateTimeOffset HeaderDeadline(
        HttpResponseMessage response,
        DateTimeOffset now,
        double baseSpacing
    )
    {
        var due = now.AddSeconds(baseSpacing);
        if (response.Headers.RetryAfter?.Delta is { } delta)
            due = Max(due, now + delta);
        if (response.Headers.RetryAfter?.Date is { } date)
            due = Max(due, date);
        string Header(string key) =>
            response.Headers.TryGetValues(key, out var values) ? string.Join(',', values) : "";
        foreach (
            var rule in Header("X-Rate-Limit-Rules")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        )
        {
            var limits = Header("X-Rate-Limit-" + rule).Split(',');
            var states = Header("X-Rate-Limit-" + rule + "-State").Split(',');
            foreach (var limit in limits)
            {
                var l = limit.Split(':');
                if (
                    l.Length != 3
                    || !int.TryParse(l[0], out int max)
                    || !int.TryParse(l[1], out int period)
                    || max <= 0
                    || period <= 0
                )
                    continue;
                // Match the period, not header order. All applicable windows count.
                foreach (var state in states)
                {
                    var s = state.Split(':');
                    if (
                        s.Length != 3
                        || !int.TryParse(s[0], out int used)
                        || !int.TryParse(s[1], out int statePeriod)
                        || statePeriod != period
                        || !int.TryParse(s[2], out int blocked)
                    )
                        continue;
                    if (blocked > 0)
                        due = Max(due, now.AddSeconds(blocked + 1));
                    // Keep one request in reserve. Do not discard 15% of a large
                    // long-term window by pausing for the entire period.
                    if (
                        blocked == 0
                        && response.StatusCode != HttpStatusCode.TooManyRequests
                        && used >= Math.Max(1, max - 1)
                    )
                        due = Max(due, now.AddSeconds(period + 1));
                    else if (
                        blocked == 0
                        && response.StatusCode != HttpStatusCode.TooManyRequests
                        && used >= max * .70
                    )
                        due = Max(due, now.AddSeconds(period / (max * .85)));
                }
            }
        }
        return due;
    }

    private void Observe(HttpResponseMessage response)
    {
        var now = DateTimeOffset.UtcNow;
        // Recompute from current state so a recovered window does not permanently
        // impose its old cadence on every later request.
        next = Max(next, HeaderDeadline(response, now, spacingSeconds));
        if (next - now > TimeSpan.FromSeconds(5))
            PersistCooldown();
    }

    private void PersistCooldown()
    {
        if (cooldownPath is not null)
            try
            {
                JsonFiles.Write(cooldownPath, next);
            }
            catch (IOException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Could not persist market cooldown; the current process still honors its deadline."
                );
            }
    }

    public void Dispose()
    {
        client.Dispose();
        gate.Dispose();
    }
}

public sealed class PriceCache
{
    private readonly string directory;

    public PriceCache(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(string key) =>
        Path.Combine(
            directory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json"
        );

    public PriceQuote? Read(string key)
    {
        try
        {
            var q = JsonFiles.Read<PriceQuote>(PathFor(key));
            var age = DateTimeOffset.UtcNow - q.RetrievedAt;
            return age < TimeSpan.Zero || age > TimeSpan.FromHours(1)
                ? null
                : q with
                {
                    Stale = age > TimeSpan.FromMinutes(q.Source == "PoE2Scout" ? 10 : 15),
                };
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Write(string key, PriceQuote quote) => JsonFiles.Write(PathFor(key), quote);

    public static string Key(string league, string id, TooltipInfo? tooltip = null) =>
        "r3|"
        + league
        + "|"
        + id
        + "|"
        + (
            tooltip is null
                ? "representative"
                : System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        tooltip.Corrupted,
                        tooltip.Complete,
                        Mods = tooltip.Mods.OrderBy(p => p.Key),
                    },
                    JsonFiles.Options
                )
        );
}

public sealed class ScoutMarket : IDisposable
{
    private readonly MarketHttp http;
    private string activeLeague = "";
    private DateTimeOffset catalogTime;
    private JsonNode? refs,
        categories;
    private readonly Dictionary<string, (DateTimeOffset time, JsonNode[] rows)> categoryCache = [];
    public Rate? ExchangeRate { get; private set; }
    private readonly MarketStore? store;

    private sealed record ContextData(JsonNode? Refs, JsonNode? Categories);

    public ScoutMarket(MarketHttp? http = null, MarketStore? store = null)
    {
        this.http = http ?? new("https://api.poe2scout.com");
        this.store = store ?? (http is null ? new(MarketStore.DefaultPath) : null);
    }

    private static string Base(string league) => "/poe2/Leagues/" + Uri.EscapeDataString(league);

    public async Task<string[]> LeaguesAsync(CancellationToken token) =>
        (await http.RequestAsync("/poe2/Leagues", token))
            .Rows()
            .Select(n => n.Text("value"))
            .Where(s => s.Length > 0)
            .ToArray();

    private async Task Context(string league, CancellationToken token)
    {
        if (
            league == activeLeague
            && DateTimeOffset.UtcNow - catalogTime < TimeSpan.FromMinutes(10)
        )
            return;
        var saved = store?.Read<ContextData>("scout/context/" + league, TimeSpan.FromMinutes(10));
        var r =
            saved?.Data.Refs
            ?? await http.RequestAsync(Base(league) + "/ReferenceCurrencies", token);
        var c =
            saved?.Data.Categories
            ?? await http.RequestAsync(Base(league) + "/Items/Categories", token);
        if (saved is null)
            store?.Write("scout/context/" + league, new ContextData(r, c));
        if (league != activeLeague)
            categoryCache.Clear();
        refs = r;
        categories = c;
        activeLeague = league;
        catalogTime = saved?.RetrievedAt ?? DateTimeOffset.UtcNow;
        var ex = refs.Rows()
            .FirstOrDefault(n => n.Text("apiId") == "exalted" || n.Text("text") == "Exalted Orb")
            ?.Number("relativePrice");
        var div = refs.Rows()
            .FirstOrDefault(n => n.Text("apiId") == "divine" || n.Text("text") == "Divine Orb")
            ?.Number("relativePrice");
        ExchangeRate = ex > 0 && div > 0 ? new(div.Value / ex.Value, catalogTime) : null;
    }

    public async Task<PriceQuote?> QuoteAsync(
        CatalogItem item,
        string league,
        CancellationToken token
    )
    {
        await Context(league, token);
        var reference =
            refs.Rows()
                .FirstOrDefault(n =>
                    n.Text("apiId") == "exalted" || n.Text("text") == "Exalted Orb"
                ) ?? throw new InvalidDataException("엑잘 환산 기준 없음");
        var referenceId = reference.Text("apiId");
        if (referenceId.Length == 0)
            referenceId = reference.Text("baseItemTypeId");
        var ordered = categories
            .Get("currencyCategories")
            .Rows()
            .Select(c => c.Text("apiId"))
            .OrderBy(c =>
                c == "currency" ? 0
                : c == "ritual" ? 1
                : 2
            );
        foreach (var category in ordered)
        {
            string categoryKey = "scout/category/" + league + "/" + referenceId + "/" + category;
            if (
                !categoryCache.ContainsKey(category)
                && store?.Read<JsonNode[]>(categoryKey, TimeSpan.FromMinutes(10)) is { } stored
            )
                categoryCache[category] = (stored.RetrievedAt, stored.Data);
            if (
                !categoryCache.TryGetValue(category, out var cached)
                || DateTimeOffset.UtcNow - cached.time > TimeSpan.FromMinutes(10)
            )
            {
                var rows = new List<JsonNode>();
                int? total = null,
                    pages = null;
                for (int page = 1; page <= 20; page++)
                {
                    var data = await http.RequestAsync(
                        Base(league)
                            + $"/Currencies/ByCategory?category={Uri.EscapeDataString(category)}&page={page}&perPage=250&dataPoints=8&frequencyHours=1&referenceCurrency={Uri.EscapeDataString(referenceId)}",
                        token
                    );
                    int current = (int)(data.Number("currentPage") ?? 0),
                        p = (int)(data.Number("pages") ?? 0),
                        t = (int)(data.Number("total") ?? -1);
                    if (
                        current != page
                        || p > 20
                        || p < 0
                        || t < 0
                        || (pages.HasValue && (p != pages || t != total))
                    )
                        throw new InvalidDataException("Scout 페이지 자료가 변경되었습니다.");
                    pages = p;
                    total = t;
                    rows.AddRange(data.Get("items").Rows());
                    if (page >= p)
                        break;
                }
                if (rows.Count != total)
                    throw new InvalidDataException("Scout 불완전한 시세 자료");
                cached = (DateTimeOffset.UtcNow, rows.ToArray());
                categoryCache[category] = cached;
                store?.Write(categoryKey, cached.rows, cached.time);
            }
            var matches = cached
                .rows.Where(r =>
                    TooltipParser.Key(r.Text("text")) == TooltipParser.Key(item.NameEn)
                )
                .ToArray();
            if (matches.Length == 1 && matches[0].Number("currentPrice") is >= 0 and var price)
                return new(
                    item.Id,
                    league,
                    price,
                    "exalted",
                    cached.time,
                    "PoE2Scout",
                    "대표 시세",
                    1
                );
        }
        return null;
    }

    public async Task<Rate?> RateAsync(string league, CancellationToken token)
    {
        await Context(league, token);
        return ExchangeRate;
    }

    public void Dispose() => http.Dispose();
}

public record Listing(
    string Seller,
    decimal Price,
    string Currency,
    string[] Mods,
    bool Corrupted,
    string Name,
    string TypeLine
);

public sealed class TradeMarket : IDisposable
{
    private readonly MarketHttp http;
    private readonly Dictionary<string, (DateTimeOffset At, Listing[] Listings)> snapshots = [];
    private readonly MarketStore? store;

    public TradeMarket(MarketHttp? http = null, MarketStore? store = null)
    {
        this.http = http ?? new("https://www.pathofexile.com");
        this.store = store ?? (http is null ? new(MarketStore.DefaultPath) : null);
    }

    public async Task<(PriceQuote? Quote, Listing[] Listings)> QuoteAsync(
        CatalogItem item,
        string league,
        Rate? rate,
        TooltipInfo? tooltip,
        CancellationToken token
    )
    {
        string snapshotKey = league + "|" + item.Id;
        if (
            !snapshots.ContainsKey(snapshotKey)
            && store?.Read<Listing[]>("trade/listings/" + snapshotKey, TimeSpan.FromMinutes(15))
                is { } stored
        )
            snapshots[snapshotKey] = (stored.RetrievedAt, stored.Data);
        if (
            snapshots.TryGetValue(snapshotKey, out var snapshot)
            && DateTimeOffset.UtcNow - snapshot.At
                < TimeSpan.FromMinutes(snapshot.Listings.Length == 0 ? 3 : 15)
        )
            return await Reprice(
                item,
                league,
                rate,
                tooltip,
                snapshot.Listings,
                snapshot.At,
                token
            );
        var query = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["status"] = new JsonObject { ["option"] = "online" },
                ["name"] = item.NameEn,
                ["stats"] = new JsonArray(
                    new JsonObject { ["type"] = "and", ["filters"] = new JsonArray() }
                ),
                ["filters"] = new JsonObject
                {
                    ["type_filters"] = new JsonObject
                    {
                        ["filters"] = new JsonObject
                        {
                            ["rarity"] = new JsonObject { ["option"] = "unique" },
                        },
                    },
                },
            },
            ["sort"] = new JsonObject { ["price"] = "asc" },
        };
        var found = await http.RequestAsync(
            "/api/trade2/search/" + Uri.EscapeDataString(league),
            token,
            query
        );
        var id = found.Text("id");
        if (id.Length == 0)
            throw new InvalidDataException("거래 검색 ID 없음");
        var ids = found.Get("result").Rows().Select(n => n.ToString()).Take(30).ToArray();
        var listings = new List<Listing>();
        foreach (var batch in ids.Chunk(10))
        {
            var response = await http.RequestAsync(
                "/api/trade2/fetch/"
                    + string.Join(',', batch.Select(Uri.EscapeDataString))
                    + "?query="
                    + Uri.EscapeDataString(id),
                token
            );
            foreach (var row in response.Get("result").Rows())
            {
                var listing = row.Get("listing");
                var data = row.Get("item");
                var price = listing.Get("price");
                var amount = price.Number("amount");
                var seller = listing.Get("account").Text("name");
                if (
                    amount is null or <= 0
                    || seller.Length == 0
                    || listing.Get("account").Get("online") is null
                    || TooltipParser.Key(data.Text("name")) != TooltipParser.Key(item.NameEn)
                )
                    continue;
                listings.Add(
                    new(
                        seller,
                        amount.Value,
                        price.Text("currency"),
                        data.Get("explicitMods").Rows().Select(PlainMod).ToArray(),
                        data.Text("corrupted").Equals("true", StringComparison.OrdinalIgnoreCase),
                        data.Text("name"),
                        data.Text("typeLine")
                    )
                );
            }
            // The representative is the cheapest ten distinct sellers. Fetch
            // another page only if invalid/duplicate/unconvertible listings mean
            // the current pages cannot supply those ten sellers.
            if (
                listings
                    .Where(l =>
                        l.Currency == "exalted"
                        || (l.Currency == "divine" && rate?.ExaltedPerDivine > 0)
                    )
                    .Select(l => l.Seller)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() >= 10
            )
                break;
        }
        var saved = listings.ToArray();
        snapshots[snapshotKey] = (DateTimeOffset.UtcNow, saved);
        store?.Write("trade/listings/" + snapshotKey, saved);
        return await Reprice(item, league, rate, tooltip, saved, DateTimeOffset.UtcNow, token);
    }

    private sealed record ProbeSnapshot(Listing[] Listings, string Reason);

    private async Task<(PriceQuote? Quote, Listing[] Listings)> Reprice(
        CatalogItem item,
        string league,
        Rate? rate,
        TooltipInfo? tooltip,
        Listing[] listings,
        DateTimeOffset observedAt,
        CancellationToken token
    )
    {
        var quote = Estimate(item.Id, league, listings, rate, tooltip, item);
        if (quote is not null)
            quote = quote with { RetrievedAt = observedAt };
        var extremes = OptionValuation.ExtremeConditions(item, tooltip);
        if (
            extremes.Length == 0
            || tooltip is null
            || listings
                .Where(l =>
                    l.Corrupted == tooltip.Corrupted
                    && OptionValuation.Similar(l.Mods, tooltip.Mods, item)
                )
                .Select(l => l.Seller)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() >= 5
        )
            return (quote, listings);
        string key = "trade/probe/" + OptionValuation.ProbeKey(league, item, tooltip);
        if (store?.Read<ProbeSnapshot>(key, TimeSpan.FromMinutes(30)) is { } old)
        {
            var local = Estimate(item.Id, league, old.Data.Listings, rate, tooltip, item);
            return (
                local is null
                    ? quote
                    : local with
                    {
                        RetrievedAt = old.RetrievedAt,
                        Note = local.Note ?? old.Data.Reason,
                    },
                old.Data.Listings.Length == 0 ? listings : old.Data.Listings
            );
        }
        var stats = store?.Read<JsonNode>("trade/stat-definitions", TimeSpan.FromDays(1))?.Data;
        if (stats is null)
        {
            stats = await http.RequestAsync("/api/trade2/data/stats", token);
            store?.Write("trade/stat-definitions", stats);
        }
        var definitions = stats
            .Get("result")
            .Rows()
            .SelectMany(g => g.Get("entries").Rows())
            .Where(s => s.Text("type") == "explicit" || s.Text("id").StartsWith("explicit."))
            .ToArray();
        var filters = new JsonArray();
        foreach (var condition in extremes)
        {
            var matches = definitions
                .Where(d => TooltipParser.ModKey(d.Text("text")) == condition.ModKey)
                .ToArray();
            if (matches.Length != 1)
                continue;
            filters.Add(
                new JsonObject
                {
                    ["id"] = matches[0].Text("id"),
                    ["value"] = new JsonObject { ["min"] = condition.Min, ["max"] = condition.Max },
                }
            );
        }
        if (filters.Count == 0)
        {
            store?.Write(key, new ProbeSnapshot([], "옵션 검색 정의 불명확 · 로컬 대표 호가 유지"));
            return (
                quote is null
                    ? null
                    : quote with
                    {
                        Note = "옵션 검색 정의 불명확 · 로컬 대표 호가 유지",
                    },
                listings
            );
        }
        var query = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["status"] = new JsonObject { ["option"] = "online" },
                ["name"] = item.NameEn,
                ["stats"] = new JsonArray(
                    new JsonObject { ["type"] = "and", ["filters"] = filters }
                ),
                ["filters"] = new JsonObject
                {
                    ["type_filters"] = new JsonObject
                    {
                        ["filters"] = new JsonObject
                        {
                            ["rarity"] = new JsonObject { ["option"] = "unique" },
                        },
                    },
                    ["misc_filters"] = new JsonObject
                    {
                        ["filters"] = new JsonObject
                        {
                            ["corrupted"] = new JsonObject
                            {
                                ["option"] = tooltip.Corrupted ? "true" : "false",
                            },
                        },
                    },
                },
            },
            ["sort"] = new JsonObject { ["price"] = "asc" },
        };
        var found = await http.RequestAsync(
            "/api/trade2/search/" + Uri.EscapeDataString(league),
            token,
            query
        );
        var searchId = found.Text("id");
        if (searchId.Length == 0)
            throw new InvalidDataException("거래 검색 ID 없음");
        var ids = found.Get("result").Rows().Select(n => n.ToString()).Take(20).ToArray();
        var extra = new List<Listing>();
        foreach (var batch in ids.Chunk(10))
        {
            var response = await http.RequestAsync(
                "/api/trade2/fetch/"
                    + string.Join(',', batch.Select(Uri.EscapeDataString))
                    + "?query="
                    + Uri.EscapeDataString(searchId),
                token
            );
            foreach (var row in response.Get("result").Rows())
            {
                var listing = row.Get("listing");
                var data = row.Get("item");
                var price = listing.Get("price");
                var amount = price.Number("amount");
                var seller = listing.Get("account").Text("name");
                if (
                    amount is null or <= 0
                    || seller.Length == 0
                    || listing.Get("account").Get("online") is null
                    || TooltipParser.Key(data.Text("name")) != TooltipParser.Key(item.NameEn)
                )
                    continue;
                extra.Add(
                    new(
                        seller,
                        amount.Value,
                        price.Text("currency"),
                        data.Get("explicitMods").Rows().Select(PlainMod).ToArray(),
                        data.Text("corrupted").Equals("true", StringComparison.OrdinalIgnoreCase),
                        data.Text("name"),
                        data.Text("typeLine")
                    )
                );
            }
        }
        // Do not let a small expensive sample replace a representative quote.
        var combined = listings.Concat(extra).ToArray();
        var refined = Estimate(item.Id, league, combined, rate, tooltip, item);
        store?.Write(key, new ProbeSnapshot(combined, "극단 옵션 범위 확인 · 호가 기준"));
        return (
            refined is null
                ? quote
                : refined with
                {
                    Note = refined.Note ?? "극단 옵션 범위 확인 · 호가 기준",
                },
            combined
        );
    }

    public static PriceQuote? Estimate(
        string id,
        string league,
        IEnumerable<Listing> input,
        Rate? rate,
        TooltipInfo? tooltip,
        CatalogItem? catalogItem = null
    )
    {
        var rows = input
            .Select(l =>
                (
                    Listing: l,
                    Ex: l.Currency == "exalted" ? (decimal?)l.Price
                    : l.Currency == "divine" && rate?.ExaltedPerDivine > 0
                        ? l.Price * rate.ExaltedPerDivine
                    : null
                )
            )
            .ToArray();
        var convertible = rows.Where(r => r.Ex.HasValue).ToArray();
        string currency = "exalted";
        string basis = "대표 호가";
        string? note = null;
        var source = convertible.Select(r => (r.Listing, Price: r.Ex!.Value)).ToArray();
        if (source.Length > 0 && source.Length < rows.Length)
            note = "환산할 수 없는 통화의 매물 제외";
        if (source.Length == 0)
        {
            var single = rows.GroupBy(r => r.Listing.Currency)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (single is null)
                return null;
            currency = single.Key;
            source = single.Select(r => (r.Listing, r.Listing.Price)).ToArray();
            note = "환율 없음 · 원래 단위";
        }
        if (tooltip is { Complete: true })
        {
            var similar = source
                .Where(r =>
                    r.Listing.Corrupted == tooltip.Corrupted
                    && (
                        catalogItem is null
                            ? Similar(r.Listing.Mods, tooltip.Mods)
                            : OptionValuation.Similar(r.Listing.Mods, tooltip.Mods, catalogItem)
                    )
                )
                .ToArray();
            if (
                similar
                    .Select(r => r.Listing.Seller)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() >= 5
            )
            {
                source = similar;
                basis = "유사 옵션 호가";
            }
            else
                note = "유사 옵션 매물 부족 · 대표 호가 유지";
        }
        else if (tooltip is not null)
            note = "옵션 판독 불완전 · 대표 호가 유지";
        var prices = source
            .GroupBy(r => r.Listing.Seller, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Min(r => r.Price))
            .Order()
            .Take(10)
            .ToArray();
        if (prices.Length == 0)
            return null;
        if (prices.Length < 5)
            note = "매물 표본 부족" + (note is null ? "" : " · " + note);
        return new(
            id,
            league,
            Valuation.Median(prices),
            currency,
            DateTimeOffset.UtcNow,
            "PoE trade2",
            basis,
            prices.Length,
            false,
            note
        );
    }

    public static bool Similar(string[] listingMods, Dictionary<string, double[]> target)
    {
        if (target.Count == 0)
            return false;
        var observed = listingMods
            .GroupBy(TooltipParser.ModKey)
            .ToDictionary(g => g.Key, g => g.ToArray());
        if (observed.Count != target.Count)
            return false;
        foreach (var (key, values) in target)
        {
            if (!observed.TryGetValue(key, out var matches) || matches.Length != 1)
                return false;
            var actual = TooltipParser.Values(matches[0]);
            if (actual.Length != values.Length)
                return false;
            for (int i = 0; i < values.Length; i++)
                if (Math.Abs(actual[i] - values[i]) > Math.Max(1, Math.Abs(values[i]) * .15))
                    return false;
        }
        return true;
    }

    public static string PlainMod(JsonNode node)
    {
        var text = node is JsonValue ? node.ToString() : node.Text("description");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\\u([0-9a-fA-F]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()
        );
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\[([^\[\]|]+)(?:\|([^\[\]]+))?\]",
            m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value
        );
    }

    public void Dispose() => http.Dispose();
}
