using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class BehaviorTests
{
    private static PriceQuote Quote(decimal value, string currency = "exalted") =>
        new("item", "league", value, currency, DateTimeOffset.UtcNow, "test", "test", 10);

    [Theory]
    [InlineData("99.999", "ex")]
    [InlineData("100", "div")]
    [InlineData("100.001", "div")]
    public void UnitSwitchUsesUnroundedValue(string amount, string unit)
    {
        var p = Valuation.Format(
            Quote(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)),
            1,
            new(100, DateTimeOffset.UtcNow)
        );
        Assert.EndsWith(unit, p.Text);
    }

    [Fact]
    public void StackTotalCrossesDivineBoundary() =>
        Assert.Equal(
            "1.2 div",
            Valuation.Format(Quote(40), 3, new(100, DateTimeOffset.UtcNow)).Text
        );

    [Fact]
    public void MissingQuantityNeverBecomesOne() =>
        Assert.Null(Valuation.Format(Quote(40), null, null).TotalExalted);

    [Fact]
    public void MissingRateKeepsOriginalCurrency()
    {
        var p = Valuation.Format(Quote(.5m, "divine"), 2, null);
        Assert.Equal("1 div", p.Text);
        Assert.Null(p.TotalExalted);
    }

    [Fact]
    public void MissingRateCanKeepExalts() =>
        Assert.Equal("500 ex", Valuation.Format(Quote(500), 1, null).Text);

    [Fact]
    public void EfficiencyRejectsUncertainInputs()
    {
        Assert.Null(Valuation.Efficiency(100, 1000, false));
        Assert.Null(Valuation.Efficiency(100, 0, true));
        Assert.Equal(50, Valuation.Efficiency(100, 2000, true));
    }

    [Fact]
    public void GenerationRejectsLateResults()
    {
        var g = new GenerationGate();
        var old = g.Next();
        g.Next();
        Assert.False(g.Accept(old));
        Assert.True(g.Accept(g.Current));
    }

    [Fact]
    public void CacheSeparatesLeagueAndOptions()
    {
        var t = new TooltipInfo(
            "item",
            "",
            [],
            new() { ["life"] = [40] },
            100,
            null,
            false,
            true,
            ""
        );
        Assert.NotEqual(PriceCache.Key("A", "item"), PriceCache.Key("B", "item"));
        Assert.NotEqual(PriceCache.Key("A", "item"), PriceCache.Key("A", "item", t));
    }

    [Fact]
    public void DuplicateSellerDoesNotControlMedian()
    {
        var listings = Enumerable
            .Range(0, 20)
            .Select(_ => new Listing("same", 1, "exalted", [], false, "", ""))
            .Concat(
                new[]
                {
                    new Listing("b", 10, "exalted", [], false, "", ""),
                    new Listing("c", 20, "exalted", [], false, "", ""),
                }
            );
        var q = TradeMarket.Estimate("i", "l", listings, null, null);
        Assert.Equal(10, q!.UnitPrice);
        Assert.Equal(3, q.Samples);
    }

    [Fact]
    public void MixedCurrenciesAreNormalizedBeforeSorting()
    {
        var q = TradeMarket.Estimate(
            "i",
            "l",
            [new("a", 1, "divine", [], false, "", ""), new("b", 10, "exalted", [], false, "", "")],
            new(100, DateTimeOffset.UtcNow),
            null
        );
        Assert.Equal(55, q!.UnitPrice);
    }

    [Fact]
    public void SimilarOptionsRequireCompleteMatchingModSet()
    {
        var target = new Dictionary<string, double[]>
        {
            { TooltipParser.ModKey("+40 to maximum Life"), [40] },
        };
        Assert.True(TradeMarket.Similar(["+42 to maximum Life"], target));
        Assert.False(TradeMarket.Similar(["+80 to maximum Life"], target));
        Assert.False(TradeMarket.Similar(["+42 to maximum Life", "+10 to Strength"], target));
    }

    [Fact]
    public void ObjectAndStringTradeModsSharePlainText()
    {
        Assert.Equal(
            "+40 to maximum Life",
            TradeMarket.PlainMod(
                JsonNode.Parse("{\"description\":\"\\\\u002B40 to maximum [Life]\"}")!
            )
        );
        Assert.Equal(
            "Adds 4 to 9 Chaos Damage to Attacks",
            TradeMarket.PlainMod(
                JsonValue.Create("Adds 4 to 9 [Chaos] Damage to [Attack|Attacks]")!
            )
        );
    }

    [Fact]
    public void TooltipParsesPurchaseAndPreservesDeferSeparately()
    {
        var item = new CatalogItem(
            "X",
            "Spiny",
            "가시털멧돼지",
            "unique",
            "",
            2,
            3,
            "",
            "",
            ["+(40 — 60) to maximum Life"],
            ["생명력 최대치 +(40 — 60)"]
        );
        var result = TooltipParser.Parse(
            ["가시털멧돼지", "생명력 최대치 +43", "비용:", "공물 점수 x1,000", "아이템 구입"],
            new("v", DateTimeOffset.UtcNow, [item])
        );
        Assert.Equal(1000, result!.PurchaseTribute);
        Assert.Null(result.DeferTribute);
        Assert.True(result.Complete);
        Assert.Equal(43, result.Mods.Values.Single()[0]);
    }

    [Fact]
    public void AmbiguousTooltipDoesNotBind()
    {
        var a = new CatalogItem("a", "Same", "같음", "unique", "", 1, 1, "", "", [], []);
        var b = a with { Id = "b" };
        Assert.Null(TooltipParser.Parse(["같음"], new("v", DateTimeOffset.UtcNow, [a, b])));
    }

    [Fact]
    public void KoreanPerSecondLiteralIsNotAnEnglishRoll()
    {
        Assert.Equal(
            new[] { 4.3 },
            TooltipParser.TranslateNumbers(
                "1초마다 생명력 (3 — 5) 재생",
                "(3 — 5) Life Regeneration per second",
                "1초마다 생명력 4.3 재생"
            )
        );
    }

    [Fact]
    public void HdrNormalizationHandlesInvalidAndNegativeSamples()
    {
        Assert.Equal(0, HdrToneMap.Channel(float.NaN));
        Assert.Equal(0, HdrToneMap.Channel(-1));
        Assert.Equal(255, HdrToneMap.Channel(1));
        Assert.InRange(HdrToneMap.Channel(.18f), 117, 119);
        Assert.Equal(HdrToneMap.Channel(.5f), HdrToneMap.Channel(1, 2));
    }

    [Fact]
    public void JsonAcceptsScoutsPascalCase()
    {
        var data = JsonNode.Parse("{\"CurrentPrice\":123.5,\"Text\":\"Divine Orb\"}");
        Assert.Equal(123.5m, data.Number("currentPrice"));
        Assert.Equal("Divine Orb", data.Text("text"));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        )
        {
            Count++;
            return Task.FromResult(response(request));
        }
    }

    [Fact]
    public async Task RateLimitCooldownPreventsImmediateRetry()
    {
        var handler = new Handler(_ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.RetryAfter = new(TimeSpan.FromSeconds(60));
            return r;
        });
        using var http = new MarketHttp("https://example.invalid", handler);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => http.RequestAsync("/test", default)
        );
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => http.RequestAsync("/test", default)
        );
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task HtmlAndAuthErrorsNeverBecomePrices()
    {
        using var html = new MarketHttp(
            "https://example.invalid",
            new Handler(_ =>
                new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html>challenge</html>",
                        Encoding.UTF8,
                        "text/html"
                    ),
                }
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(() => html.RequestAsync("/test", default));
        var handler = new Handler(_ => new(HttpStatusCode.Forbidden));
        using var denied = new MarketHttp("https://example.invalid", handler);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => denied.RequestAsync("/test", default)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => denied.RequestAsync("/test", default)
        );
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public void OldCacheIsLabeledAndExpiredCacheIsRejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ritual-test-" + Guid.NewGuid());
        try
        {
            var cache = new PriceCache(dir);
            cache.Write("a", Quote(1) with { RetrievedAt = DateTimeOffset.UtcNow.AddMinutes(-16) });
            Assert.True(cache.Read("a")!.Stale);
            cache.Write("a", Quote(1) with { RetrievedAt = DateTimeOffset.UtcNow.AddHours(-2) });
            Assert.Null(cache.Read("a"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
