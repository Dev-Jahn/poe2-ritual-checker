using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Ritual.Core;

public record NinjaEntry(
    string Name,
    string Kind,
    decimal Price,
    string Currency,
    bool Corrupted,
    string Variant,
    int Samples
);

public record NinjaSnapshot(
    DateTimeOffset CheckedAt,
    DateTimeOffset NextCheck,
    string? ETag,
    NinjaEntry[] Entries,
    Rate? Rate
);

public record NinjaLookup(PriceQuote? Quote, string Status);

public sealed class NinjaMarket : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    public static readonly string[] ExchangeTypes =
    [
        "Currency",
        "Ritual",
        "Fragments",
        "Abyss",
        "UncutGems",
        "LineageSupportGems",
        "Essences",
        "SoulCores",
        "Idols",
        "Runes",
        "Expedition",
        "Delirium",
        "Breach",
        "Verisium",
    ];
    public static readonly string[] UniqueTypes =
    [
        "UniqueWeapons",
        "UniqueArmours",
        "UniqueAccessories",
        "UniqueFlasks",
        "UniqueCharms",
        "UniqueJewels",
        "UniqueSanctumRelics",
        "UniqueTablets",
        "PrecursorTablets",
    ];
    public static IEnumerable<string> Categories => ExchangeTypes.Concat(UniqueTypes);
    private readonly HttpClient client;
    private readonly MarketStore store;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan spacing;
    private readonly SemaphoreSlim gate = new(1);
    private readonly ConcurrentDictionary<string, NinjaSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> retries = new();
    private readonly ConcurrentDictionary<string, string> failures = new();
    private DateTimeOffset nextRequest;
    public event Action<string>? Updated;

    public NinjaMarket(
        string? database = null,
        HttpMessageHandler? handler = null,
        Func<DateTimeOffset>? clock = null
    )
    {
        store = new MarketStore(database ?? MarketStore.DefaultPath);
        client = handler is null ? new(new HttpClientHandler { UseCookies = false }) : new(handler);
        client.BaseAddress = new Uri("https://poe.ninja");
        client.Timeout = TimeSpan.FromSeconds(20);
        client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "RitualChecker/0.2 (+https://github.com/Dev-Jahn/poe2-ritual-checker)"
        );
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        spacing = handler is null ? TimeSpan.FromSeconds(1) : TimeSpan.Zero;
        nextRequest =
            store.Read<DateTimeOffset>("ninja/backoff", TimeSpan.FromDays(1))?.Data
            ?? DateTimeOffset.MinValue;
    }

    private static string Key(string league, string category) =>
        "ninja/v1/" + league + "/" + category;

    public void Load(string league)
    {
        foreach (var category in Categories)
        {
            var key = Key(league, category);
            if (
                !snapshots.ContainsKey(key)
                && store.Read<NinjaSnapshot>(key, TimeSpan.MaxValue) is { } saved
            )
                snapshots.TryAdd(key, saved.Data);
        }
    }

    public async Task<string[]> LeaguesAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var old = store.Read<string[]>("ninja/leagues", RefreshInterval);
            if (old is not null)
                return old.Data;
            await WaitForRequest(token);
            using var response = await client.GetAsync("/poe2/api/economy/leagues", token);
            CheckStatus(response);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
            var leagues = body.Rows()
                .Select(x => x.Text("id"))
                .Where(x => x.Length > 0)
                .Distinct()
                .ToArray();
            if (leagues.Length == 0)
                throw new InvalidDataException("poe.ninja 리그 응답 형식 변경");
            store.Write("ninja/leagues", leagues);
            return leagues;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RefreshAsync(string league, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(league))
            return;
        await gate.WaitAsync(token);
        try
        {
            Load(league);
            foreach (var category in Categories)
            {
                token.ThrowIfCancellationRequested();
                string key = Key(league, category);
                snapshots.TryGetValue(key, out var old);
                if (old?.NextCheck > clock() || retries.GetValueOrDefault(key) > clock())
                    continue;
                try
                {
                    await WaitForRequest(token);
                    string path = ExchangeTypes.Contains(category)
                        ? "exchange/current"
                        : "stash/current/item";
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"/poe2/api/economy/{path}/overview?league={Uri.EscapeDataString(league)}&type={category}"
                    );
                    if (old?.ETag is not null)
                        request.Headers.TryAddWithoutValidation("If-None-Match", old.ETag);
                    using var response = await client.SendAsync(request, token);
                    CheckStatus(response);
                    var now = clock();
                    var remaining =
                        (response.Headers.CacheControl?.MaxAge ?? TimeSpan.Zero)
                        - (response.Headers.Age ?? TimeSpan.Zero);
                    var nextCheck =
                        now + (remaining > RefreshInterval ? remaining : RefreshInterval);
                    NinjaSnapshot snapshot;
                    if (response.StatusCode == HttpStatusCode.NotModified && old is not null)
                        snapshot = old with
                        {
                            CheckedAt = now,
                            NextCheck = nextCheck,
                            Rate =
                                old.Rate?.RetrievedAt == old.CheckedAt
                                    ? old.Rate with
                                    {
                                        RetrievedAt = now,
                                        Stale = false,
                                    }
                                    : old.Rate,
                        };
                    else
                    {
                        var json =
                            JsonNode.Parse(await response.Content.ReadAsStringAsync(token))
                            ?? throw new InvalidDataException("빈 시세 응답");
                        snapshot = Parse(
                            json,
                            category,
                            now,
                            nextCheck,
                            response.Headers.NonValidated.TryGetValues("ETag", out var tags)
                                ? tags.FirstOrDefault()
                                : null
                        );
                        // A response without an exchange rate does not erase the last observed one.
                        snapshot = snapshot with
                        {
                            Rate = snapshot.Rate ?? old?.Rate ?? GetRate(league),
                        };
                    }
                    snapshots[key] = snapshot;
                    if (response.Headers.CacheControl?.NoStore != true)
                        store.Write(key, snapshot);
                    failures.TryRemove(key, out _);
                    retries.TryRemove(key, out _);
                    Updated?.Invoke(league);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (MarketCooldownException)
                {
                    failures[key] = "시세 서버 대기";
                    break;
                }
                catch (Exception e)
                    when (e
                            is HttpRequestException
                                or TaskCanceledException
                                or System.Text.Json.JsonException
                                or InvalidDataException
                    )
                {
                    retries[key] = clock().AddMinutes(2);
                    failures[key] = "시세 갱신 실패";
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WaitForRequest(CancellationToken token)
    {
        if (nextRequest > clock() + spacing)
            throw new MarketCooldownException(nextRequest);
        var delay = nextRequest - clock();
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, token);
        nextRequest = clock() + spacing;
    }

    private void CheckStatus(HttpResponseMessage response)
    {
        if (
            response.StatusCode == HttpStatusCode.TooManyRequests
            || response.Headers.RetryAfter is not null && !response.IsSuccessStatusCode
        )
        {
            nextRequest =
                response.Headers.RetryAfter?.Date
                ?? clock() + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            if (nextRequest <= clock())
                nextRequest = clock().AddSeconds(1);
            store.Write("ninja/backoff", nextRequest);
            throw new MarketCooldownException(nextRequest);
        }
        if (response.StatusCode != HttpStatusCode.NotModified)
            response.EnsureSuccessStatusCode();
    }

    public static NinjaSnapshot Parse(
        JsonNode json,
        string category,
        DateTimeOffset now,
        DateTimeOffset nextCheck,
        string? etag
    )
    {
        if (json.Get("lines") is not JsonArray || json.Get("core") is not JsonObject)
            throw new InvalidDataException("poe.ninja 응답 형식 변경");
        var core = json.Get("core");
        string currency = core.Text("primary");
        if (currency.Length == 0)
            throw new InvalidDataException("시세 기준 통화 없음");
        decimal? PerPrimary(string id) => id == currency ? 1 : core.Get("rates").Number(id);
        Rate? rate =
            PerPrimary("exalted") is > 0 and var ex && PerPrimary("divine") is > 0 and var div
                ? new(ex / div, now)
                : null;
        decimal scale =
            currency is not ("divine" or "exalted")
            && PerPrimary("exalted") is > 0 and var unitsPerPrimary
                ? unitsPerPrimary
                : 1;
        string quoteCurrency =
            currency is not ("divine" or "exalted") && PerPrimary("exalted") is > 0
                ? "exalted"
                : currency;
        bool exchange = ExchangeTypes.Contains(category);
        var names = json.Get("items")
            .Rows()
            .Concat(core.Get("items").Rows())
            .GroupBy(x => x.Text("id"))
            .ToDictionary(g => g.Key, g => g.First().Text("name"));
        var entries = json.Get("lines")
            .Rows()
            .Where(x => x.Number("primaryValue") is > 0)
            .Select(x => new NinjaEntry(
                exchange ? names.GetValueOrDefault(x.Text("id"), "") : x.Text("name"),
                exchange ? "currency" : "unique",
                x.Number("primaryValue")!.Value * scale,
                quoteCurrency,
                x.Text("corrupted").Equals("true", StringComparison.OrdinalIgnoreCase),
                (x.Text("variant") + "|" + x.Text("baseType")).Trim('|'),
                (int)(x.Number("listingCount") ?? 0)
            ))
            .Where(x => x.Name.Length > 0)
            .ToList();
        if (category == "Currency")
            foreach (var item in core.Get("items").Rows())
                if (
                    !entries.Any(e => Normalize(e.Name) == Normalize(item.Text("name")))
                    && PerPrimary(item.Text("id")) is > 0 and var units
                )
                    entries.Add(
                        new(
                            item.Text("name"),
                            "currency",
                            scale / units,
                            quoteCurrency,
                            false,
                            "",
                            0
                        )
                    );
        return new(now, nextCheck, etag, entries.ToArray(), rate);
    }

    internal static string Normalize(string name) =>
        name.Normalize(NormalizationForm.FormKC).Replace('’', '\'').Trim().ToUpperInvariant();

    public Rate? GetRate(string league)
    {
        var rate = Categories
            .Select(c => snapshots.GetValueOrDefault(Key(league, c))?.Rate)
            .Where(r => r is { ExaltedPerDivine: > 0 })
            .OrderByDescending(r => r!.RetrievedAt)
            .FirstOrDefault();
        return MarkRateAge(rate);
    }

    private Rate? MarkRateAge(Rate? rate) =>
        rate is not { ExaltedPerDivine: > 0 }
            ? null
            : rate with
            {
                Stale = clock() - rate.RetrievedAt > RefreshInterval,
            };

    public NinjaLookup GetQuote(CatalogItem item, string league, TooltipInfo? tooltip = null)
    {
        var categories = item.Kind == "currency" ? ExchangeTypes : UniqueTypes;
        var available = categories
            .Select(c =>
                (Key: Key(league, c), Snapshot: snapshots.GetValueOrDefault(Key(league, c)))
            )
            .ToArray();
        var matches = available
            .Where(x => x.Snapshot is not null)
            .SelectMany(x =>
                x.Snapshot!.Entries.Where(e =>
                        e.Kind == item.Kind
                        && Normalize(e.Name) == Normalize(item.NameEn)
                        && (item.Kind == "currency" || e.Corrupted == (tooltip?.Corrupted ?? false))
                    )
                    .Select(e => (Entry: e, Snapshot: x.Snapshot!))
            )
            .ToArray();
        if (matches.Length == 0)
            return new(
                null,
                available.Any(x => failures.ContainsKey(x.Key)) ? "시세 갱신 실패"
                    : available.Any(x => x.Snapshot is null) ? "시세 준비 중"
                    : "poe.ninja 시세 없음"
            );
        bool variantUncertain =
            matches.Select(x => (x.Entry.Variant, x.Entry.Currency)).Distinct().Count() > 1;
        var chosen = matches
            .OrderBy(x => clock() - x.Snapshot.CheckedAt > RefreshInterval)
            .ThenByDescending(x => x.Entry.Samples)
            .ThenByDescending(x => x.Snapshot.CheckedAt)
            .First();
        var e = chosen.Entry;
        bool stale = clock() - chosen.Snapshot.CheckedAt > RefreshInterval;
        return new(
            new(
                item.Id,
                league,
                e.Price,
                e.Currency,
                chosen.Snapshot.CheckedAt,
                "poe.ninja",
                item.Kind == "currency" ? "거래소 교환 시세" : "고유 대표 시세",
                e.Samples,
                stale,
                variantUncertain
                        ? $"기준: {e.Variant} · 같은 이름의 변형 중 표본 최다. 현재 아이템의 변형·옵션 가격은 미확인; 거래소 단축키로 조회"
                    : e.Variant.Length > 0
                        ? $"기준: {e.Variant} · 옵션별 실매물 확인은 거래소 단축키"
                    : item.Kind == "unique" ? "옵션별 실매물 확인은 거래소 단축키"
                    : null,
                Estimated: variantUncertain,
                ExchangeRate: MarkRateAge(chosen.Snapshot.Rate) ?? GetRate(league)
            ),
            ""
        );
    }

    public string Summary(string league) =>
        $"poe.ninja {Categories.Count(c => snapshots.ContainsKey(Key(league, c)))}/{Categories.Count()} 분류 · 30분 간격 갱신"
        + (failures.Keys.Any(k => k.StartsWith(Key(league, ""))) ? " · 일부 갱신 대기/실패" : "");

    public void Dispose()
    {
        client.Dispose();
        gate.Dispose();
    }
}
