using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class MarketReuseTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action)
        : HttpMessageHandler
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        )
        {
            Count++;
            return Task.FromResult(action(request));
        }
    }

    private static HttpResponseMessage Json(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task ConcurrentIdenticalReadsUseOneNetworkRequest()
    {
        var handler = new Handler(_ => Json("{\"value\":1}"));
        using var http = new MarketHttp("https://example.invalid", handler);
        var results = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => http.RequestAsync("/same", default))
        );
        Assert.Equal(1, handler.Count);
        results[0]["value"] = 9;
        Assert.Equal(1, results[1].Number("value"));
    }

    [Fact]
    public async Task SuccessfulResponseNearLimitPausesBeforeNextRequest()
    {
        var handler = new Handler(_ =>
        {
            var response = Json("{}");
            response.Headers.Add("X-Rate-Limit-Rules", "ip");
            response.Headers.Add("X-Rate-Limit-Ip", "10:60:120");
            response.Headers.Add("X-Rate-Limit-Ip-State", "9:60:0");
            return response;
        });
        using var http = new MarketHttp("https://example.invalid", handler);
        await http.RequestAsync("/a", default);
        var error = await Assert.ThrowsAsync<MarketCooldownException>(
            () => http.RequestAsync("/b", default)
        );
        Assert.True(error.RetryAt > DateTimeOffset.UtcNow.AddSeconds(55));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task SqliteListingSnapshotSurvivesNewMarketInstanceAndRepricesLocally()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ritual-store-" + Guid.NewGuid() + ".sqlite"
        );
        var store = new MarketStore(path);
        var item = new CatalogItem(
            "ring",
            "Ring",
            "반지",
            "unique",
            "",
            1,
            1,
            "",
            "",
            ["+(40 — 60) to maximum Life"],
            ["생명력 최대치 +(40 — 60)"]
        );
        var listings = Enumerable
            .Range(0, 6)
            .Select(i => new Listing(
                "seller" + i,
                10 + i,
                "exalted",
                ["+50 to maximum Life"],
                false,
                "Ring",
                "Ring"
            ))
            .ToArray();
        store.Write("trade/listings/L|ring", listings);
        var handler = new Handler(_ => throw new Exception("No network expected"));
        using var http = new MarketHttp("https://example.invalid", handler);
        using var market = new TradeMarket(http, new MarketStore(path));
        var tooltip = new TooltipInfo(
            "ring",
            "반지",
            [],
            new() { [TooltipParser.ModKey("+50 to maximum Life")] = [50] },
            100,
            null,
            false,
            true,
            ""
        );
        var result = await market.QuoteAsync(item, "L", null, tooltip, default);
        Assert.NotNull(result.Quote);
        Assert.Equal(0, handler.Count);
        Assert.Equal(6, result.Quote!.Samples);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public void SimilarityUsesRollSpanAndKeepsFixedValuesExact()
    {
        var item = new CatalogItem(
            "i",
            "I",
            "이",
            "unique",
            "",
            1,
            1,
            "",
            "",
            ["+(100 — 110) to maximum Life"],
            []
        );
        var target = new Dictionary<string, double[]>
        {
            { TooltipParser.ModKey("+100 to maximum Life"), [100] },
        };
        Assert.True(OptionValuation.Similar(["+101 to maximum Life"], target, item));
        Assert.False(OptionValuation.Similar(["+110 to maximum Life"], target, item));
    }

    [Fact]
    public void OnlyCompleteValidExtremeRollsQualifyForInvestigation()
    {
        var item = new CatalogItem(
            "i",
            "I",
            "이",
            "unique",
            "",
            1,
            1,
            "",
            "",
            ["+(40 — 60) to maximum Life"],
            []
        );
        TooltipInfo Tip(double value, bool complete = true) =>
            new(
                "i",
                "이",
                [],
                new() { [TooltipParser.ModKey("+50 to maximum Life")] = [value] },
                null,
                null,
                false,
                complete,
                ""
            );
        Assert.Empty(OptionValuation.ExtremeConditions(item, Tip(50)));
        Assert.Single(OptionValuation.ExtremeConditions(item, Tip(60)));
        Assert.Single(OptionValuation.ExtremeConditions(item, Tip(40)));
        Assert.Empty(OptionValuation.ExtremeConditions(item, Tip(99)));
        Assert.Empty(OptionValuation.ExtremeConditions(item, Tip(60, false)));
    }

    [Fact]
    public async Task ExtremeRangeIsQueriedOnceAndReusedAcrossNearbyRollsAndRestart()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ritual-probe-" + Guid.NewGuid() + ".sqlite"
        );
        try
        {
            var store = new MarketStore(path);
            var item = new CatalogItem(
                "ring",
                "Ring",
                "반지",
                "unique",
                "",
                1,
                1,
                "",
                "",
                ["+(40 — 60) to maximum Life"],
                ["생명력 최대치 +(40 — 60)"]
            );
            store.Write(
                "trade/listings/L|ring",
                new[]
                {
                    new Listing(
                        "ordinary",
                        10,
                        "exalted",
                        ["+50 to maximum Life"],
                        false,
                        "Ring",
                        "Ring"
                    ),
                }
            );
            TooltipInfo Tip(double value) =>
                new(
                    "ring",
                    "반지",
                    [],
                    new() { [TooltipParser.ModKey("+50 to maximum Life")] = [value] },
                    100,
                    null,
                    false,
                    true,
                    ""
                );
            var handler = new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/stats"))
                    return Json(
                        "{\"result\":[{\"entries\":[{\"id\":\"explicit.life\",\"type\":\"explicit\",\"text\":\"+# to maximum Life\"}]}]}"
                    );
                if (request.RequestUri.AbsolutePath.Contains("/search/"))
                {
                    var body = JsonNode.Parse(
                        request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
                    )!;
                    Assert.Equal(
                        58,
                        body["query"]!["stats"]![0]!["filters"]![0]!["value"]!.Number("min")
                    );
                    return Json("{\"id\":\"q\",\"result\":[\"a\",\"b\",\"c\",\"d\",\"e\"]}");
                }
                var rows = Enumerable
                    .Range(0, 5)
                    .Select(n => new
                    {
                        listing = new
                        {
                            account = new { name = "premium" + n, online = new { } },
                            price = new { amount = 100 + n, currency = "exalted" },
                        },
                        item = new
                        {
                            name = "Ring",
                            typeLine = "Ring",
                            corrupted = false,
                            explicitMods = new[] { "+60 to maximum Life" },
                        },
                    });
                return Json(System.Text.Json.JsonSerializer.Serialize(new { result = rows }));
            });
            using (var http = new MarketHttp("https://example.invalid", handler))
            using (var market = new TradeMarket(http, store))
            {
                var result = await market.QuoteAsync(item, "L", null, Tip(59), default);
                Assert.Equal("유사 옵션 호가", result.Quote!.Basis);
                Assert.Equal(102, result.Quote.UnitPrice);
                Assert.Equal(3, handler.Count);
            }
            var denied = new Handler(_ => throw new Exception("Cached range must not query again"));
            using (var http = new MarketHttp("https://example.invalid", denied))
            using (var market = new TradeMarket(http, new MarketStore(path)))
            {
                var result = await market.QuoteAsync(item, "L", null, Tip(60), default);
                Assert.Equal(102, result.Quote!.UnitPrice);
                Assert.Equal(0, denied.Count);
            }
            Assert.Equal(
                OptionValuation.ProbeKey("L", item, Tip(59)),
                OptionValuation.ProbeKey("L", item, Tip(60))
            );
            Assert.NotEqual(
                OptionValuation.ProbeKey("L", item, Tip(59)),
                OptionValuation.ProbeKey("Other", item, Tip(59))
            );
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ScoutReusesWholeLeagueCategoryAfterRestart()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ritual-scout-" + Guid.NewGuid() + ".sqlite"
        );
        try
        {
            var store = new MarketStore(path);
            var at = DateTimeOffset.UtcNow.AddMinutes(-4);
            store.Write(
                "scout/context/L",
                new
                {
                    Refs = JsonNode.Parse(
                        "[{\"apiId\":\"exalted\",\"relativePrice\":1},{\"apiId\":\"divine\",\"relativePrice\":250}]"
                    ),
                    Categories = JsonNode.Parse(
                        "{\"currencyCategories\":[{\"apiId\":\"ritual\"}]}"
                    ),
                },
                at
            );
            store.Write(
                "scout/category/L/exalted/ritual",
                new[]
                {
                    JsonNode.Parse("{\"text\":\"Omen A\",\"currentPrice\":2}"),
                    JsonNode.Parse("{\"text\":\"Omen B\",\"currentPrice\":3}"),
                },
                at
            );
            var handler = new Handler(_ => throw new Exception("Fresh category should be local"));
            using var http = new MarketHttp("https://example.invalid", handler);
            using var scout = new ScoutMarket(http, new MarketStore(path));
            var item = new CatalogItem(
                "a",
                "Omen A",
                "징조 A",
                "currency",
                "",
                1,
                1,
                "",
                "",
                [],
                []
            );
            Assert.Equal(2, (await scout.QuoteAsync(item, "L", default))!.UnitPrice);
            Assert.Equal(
                3,
                (
                    await scout.QuoteAsync(item with { Id = "b", NameEn = "Omen B" }, "L", default)
                )!.UnitPrice
            );
            Assert.Equal(250, (await scout.RateAsync("L", default))!.ExaltedPerDivine);
            Assert.Equal(0, handler.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
