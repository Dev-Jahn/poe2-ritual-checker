using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class NinjaMarketTests
{
    private static string Database() =>
        Path.Combine(Path.GetTempPath(), "ritual-ninja-tests", Guid.NewGuid() + ".sqlite");

    private static readonly string Payload = """
        {"core":{"primary":"divine","rates":{"exalted":250},"items":[{"id":"divine","name":"Divine Orb"},{"id":"exalted","name":"Exalted Orb"}]},"items":[{"id":"sacred-bloom","name":"Sacred Bloom"}],"lines":[{"id":"sacred-bloom","name":"Test Unique","primaryValue":0.05,"corrupted":false,"listingCount":12}]}
        """;

    private static CatalogItem Item(string name, string kind = "currency") =>
        new(name.Replace(' ', '_'), name, name, kind, "", 1, 1, "", "", [], []);

    private sealed class Handler : HttpMessageHandler
    {
        public int Count;
        public bool Unchanged;
        public bool Limited;
        public bool Failing;
        public bool NonStandardETag;
        public bool MissingRates;
        public int Conditional;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        )
        {
            Count++;
            if (request.Headers.Contains("If-None-Match"))
                Conditional++;
            var r = new HttpResponseMessage(
                Limited ? HttpStatusCode.TooManyRequests
                : Failing ? HttpStatusCode.ServiceUnavailable
                : Unchanged ? HttpStatusCode.NotModified
                : HttpStatusCode.OK
            );
            if (Limited)
                r.Headers.RetryAfter = new(TimeSpan.FromMinutes(10));
            if (NonStandardETag)
                r.Headers.TryAddWithoutValidation("ETag", "W/snapshot-1");
            else
                r.Headers.ETag = new("\"snapshot-1\"");
            if (!Unchanged)
                r.Content = new StringContent(
                    MissingRates ? Payload.Replace("\"exalted\":250", "") : Payload,
                    Encoding.UTF8,
                    "application/json"
                );
            return Task.FromResult(r);
        }
    }

    [Fact]
    public async Task StartupBatchesAllCategoriesThenServesSacredBloomWithoutRequests()
    {
        var handler = new Handler();
        using var market = new NinjaMarket(Database(), handler);
        await Task.WhenAll(
            market.RefreshAsync("League A", default),
            market.RefreshAsync("League A", default)
        );
        Assert.Equal(23, handler.Count);
        for (int i = 0; i < 100; i++)
        {
            var q = market.GetQuote(Item("Sacred Bloom"), "League A").Quote;
            Assert.Equal(.05m, q!.UnitPrice);
            Assert.Equal("divine", q.Currency);
        }
        Assert.Equal(250, market.GetRate("League A")!.ExaltedPerDivine);
        Assert.Equal(23, handler.Count);
        Assert.Null(market.GetQuote(Item("Sacred Bloom"), "League B").Quote);
    }

    [Fact]
    public async Task DiskCacheSurvivesRestartAndRevalidatesAfterThirtyMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        string database = Database();
        using (var first = new NinjaMarket(database, new Handler(), () => now))
            await first.RefreshAsync("League", default);
        var handler = new Handler { Unchanged = true };
        using var second = new NinjaMarket(database, handler, () => now);
        second.Load("League");
        Assert.NotNull(second.GetQuote(Item("Sacred Bloom"), "League").Quote);
        now += TimeSpan.FromMinutes(29);
        await second.RefreshAsync("League", default);
        Assert.Equal(0, handler.Count);
        now += TimeSpan.FromMinutes(1);
        await second.RefreshAsync("League", default);
        Assert.Equal(23, handler.Count);
        Assert.Equal(23, handler.Conditional);
        Assert.False(second.GetQuote(Item("Sacred Bloom"), "League").Quote!.Stale);
    }

    [Fact]
    public async Task FailuresKeepStalePriceAndBackoffSurvivesRestart()
    {
        var now = DateTimeOffset.UtcNow;
        string database = Database();
        var handler = new Handler();
        using (var market = new NinjaMarket(database, handler, () => now))
        {
            await market.RefreshAsync("League", default);
            now += TimeSpan.FromMinutes(31);
            handler.Limited = true;
            await market.RefreshAsync("League", default);
            Assert.Equal(24, handler.Count);
            Assert.True(market.GetQuote(Item("Sacred Bloom"), "League").Quote!.Stale);
            Assert.Equal(250, market.GetRate("League")!.ExaltedPerDivine);
            Assert.Equal(
                "12.5 ex*",
                Valuation
                    .Format(
                        market.GetQuote(Item("Sacred Bloom"), "League").Quote!,
                        1,
                        market.GetRate("League")
                    )
                    .Text
            );
        }
        var next = new Handler();
        using var restart = new NinjaMarket(database, next, () => now);
        await restart.RefreshAsync("League", default);
        Assert.Equal(0, next.Count);
        now += TimeSpan.FromMinutes(11);
        await restart.RefreshAsync("League", default);
        Assert.Equal(23, next.Count);
    }

    [Fact]
    public async Task FailedRefreshDoesNotReplaceGoodSnapshotWithEmptyPrices()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new Handler();
        using var market = new NinjaMarket(Database(), handler, () => now);
        await market.RefreshAsync("League", default);
        now += TimeSpan.FromMinutes(31);
        handler.Failing = true;
        await market.RefreshAsync("League", default);
        Assert.NotNull(market.GetQuote(Item("Sacred Bloom"), "League").Quote);
        now += TimeSpan.FromHours(4);
        var old = market.GetQuote(Item("Sacred Bloom"), "League").Quote!;
        Assert.Equal("12.5 ex*", Valuation.Format(old, 1, market.GetRate("League")).Text);
    }

    [Fact]
    public void WeekOldPriceAndItsExchangeRateSurviveDatabaseCleanupAndRestart()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-7);
        string database = Database();
        var store = new MarketStore(database);
        var snapshot = NinjaMarket.Parse(
            JsonNode.Parse(Payload)!,
            "Ritual",
            old,
            old.AddMinutes(30),
            null
        );
        store.Write("ninja/v1/League/Ritual", snapshot, old);
        store.Write("temporary-test-record", "expired", old);
        var handler = new Handler { Failing = true };
        using var market = new NinjaMarket(database, handler, () => now);
        market.Load("League");
        var quote = market.GetQuote(Item("Sacred Bloom"), "League").Quote!;
        Assert.True(quote.Stale);
        Assert.Equal(old, quote.RetrievedAt);
        Assert.Equal("12.5 ex*", Valuation.Format(quote, 1, market.GetRate("League")).Text);
        Assert.Null(market.GetQuote(Item("Sacred Bloom"), "Other league").Quote);
        Assert.Null(
            new MarketStore(database).Read<string>("temporary-test-record", TimeSpan.MaxValue)
        );
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task MissingRateInRefreshKeepsPriorRateAnd304DoesNotMakeItFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new Handler();
        using var market = new NinjaMarket(Database(), handler, () => now);
        await market.RefreshAsync("League", default);
        var rateTime = now;
        now += TimeSpan.FromMinutes(31);
        handler.MissingRates = true;
        await market.RefreshAsync("League", default);
        var quote = market.GetQuote(Item("Sacred Bloom"), "League").Quote!;
        Assert.False(quote.Stale);
        Assert.True(quote.ExchangeRate!.Stale);
        Assert.Equal("12.5 ex*", Valuation.Format(quote, 1, market.GetRate("League")).Text);
        now += TimeSpan.FromMinutes(30);
        handler.Unchanged = true;
        await market.RefreshAsync("League", default);
        Assert.Equal(rateTime, market.GetRate("League")!.RetrievedAt);
        Assert.True(market.GetRate("League")!.Stale);
        now += TimeSpan.FromMinutes(30);
        handler.Unchanged = false;
        handler.MissingRates = false;
        await market.RefreshAsync("League", default);
        Assert.False(market.GetRate("League")!.Stale);
        Assert.Equal(
            "12.5 ex",
            Valuation.Format(market.GetQuote(Item("Sacred Bloom"), "League").Quote!, 1, null).Text
        );
    }

    [Fact]
    public void UsesPrimaryCurrencyAndDoesNotInvertExchangeRate()
    {
        var json = JsonNode.Parse(Payload)!;
        json["core"]!["primary"] = "exalted";
        json["core"]!["rates"] = JsonNode.Parse("{\"divine\":0.004}");
        var snapshot = NinjaMarket.Parse(
            json,
            "Currency",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null
        );
        Assert.Equal(250, snapshot.Rate!.ExaltedPerDivine);
        Assert.Equal("exalted", snapshot.Entries.First().Currency);
        json["core"]!["rates"] = new JsonObject();
        Assert.Null(
            NinjaMarket
                .Parse(json, "Currency", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)
                .Rate
        );
    }

    [Fact]
    public async Task CorruptedUniquesRequireMatchingTooltip()
    {
        var handler = new JsonHandler(
            """
            {"core":{"primary":"divine","rates":{}},"items":[],"lines":[{"name":"Test Unique","primaryValue":2,"corrupted":true}]}
            """
        );
        using var market = new NinjaMarket(Database(), handler);
        await market.RefreshAsync("League", default);
        Assert.Null(market.GetQuote(Item("Test Unique", "unique"), "League").Quote);
        var tip = new TooltipInfo(
            "Test_Unique",
            "Test Unique",
            [],
            [],
            null,
            null,
            true,
            false,
            ""
        );
        Assert.Equal(
            2,
            market.GetQuote(Item("Test Unique", "unique"), "League", tip).Quote!.UnitPrice
        );
    }

    [Fact]
    public async Task RepresentativeUsesMostListedVariantAndMarksUncertainValue()
    {
        using var market = new NinjaMarket(
            Database(),
            new JsonHandler(
                """
                {"core":{"primary":"divine","rates":{}},"lines":[{"name":"Test Unique","baseType":"Runemastered Club","primaryValue":100,"listingCount":1},{"name":"Test Unique","baseType":"Club","primaryValue":0.01,"listingCount":1000}]}
                """
            )
        );
        await market.RefreshAsync("League", default);
        var quote = market.GetQuote(Item("Test Unique", "unique"), "League").Quote!;
        Assert.Equal(.01m, quote.UnitPrice);
        Assert.True(quote.Estimated);
        Assert.Contains("Club", quote.Note);
        Assert.StartsWith("≈", Valuation.Format(quote, 1, new(250, DateTimeOffset.UtcNow)).Text);
    }

    [Fact]
    public async Task PreservesNinjaWeakETagWithoutQuotedValue()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new Handler { NonStandardETag = true };
        using var market = new NinjaMarket(Database(), handler, () => now);
        await market.RefreshAsync("League", default);
        now += TimeSpan.FromMinutes(30);
        handler.Unchanged = true;
        await market.RefreshAsync("League", default);
        Assert.Equal(23, handler.Conditional);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }
            );
    }
}
