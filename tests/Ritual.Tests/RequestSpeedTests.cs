using System.Net;
using System.Text;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class RequestSpeedTests
{
    private static HttpResponseMessage Headers(
        string limits,
        string states,
        HttpStatusCode code = HttpStatusCode.OK
    )
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Add("X-Rate-Limit-Rules", "ip");
        response.Headers.Add("X-Rate-Limit-Ip", limits);
        response.Headers.Add("X-Rate-Limit-Ip-State", states);
        return response;
    }

    [Fact]
    public void LargeWindowWithRemainingCapacityDoesNotWaitWholePeriod()
    {
        var now = DateTimeOffset.UtcNow;
        using var response = Headers("300:600:600", "255:600:0");
        Assert.Equal(3, (MarketHttp.HeaderDeadline(response, now, 3) - now).TotalSeconds);
    }

    [Fact]
    public void RecoveryRestoresNormalCadenceAndExhaustionStillWaits()
    {
        var now = DateTimeOffset.UtcNow;
        using var busy = Headers("10:60:120", "8:60:0");
        using var recovered = Headers("10:60:120", "1:60:0");
        using var exhausted = Headers("10:60:120", "9:60:0");
        Assert.InRange((MarketHttp.HeaderDeadline(busy, now, 3) - now).TotalSeconds, 7, 8);
        Assert.Equal(3, (MarketHttp.HeaderDeadline(recovered, now, 3) - now).TotalSeconds);
        Assert.Equal(61, (MarketHttp.HeaderDeadline(exhausted, now, 3) - now).TotalSeconds);
    }

    [Fact]
    public void ActualRetryDeadlineIsNotReplacedByCountingWindow()
    {
        var now = DateTimeOffset.UtcNow;
        using var response = Headers("10:300:10", "11:300:10", HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(10));
        Assert.Equal(11, (MarketHttp.HeaderDeadline(response, now, 3) - now).TotalSeconds);
    }

    [Fact]
    public void ReorderedWindowsAreMatchedByPeriod()
    {
        var now = DateTimeOffset.UtcNow;
        using var response = Headers("10:60:120,300:600:600", "200:600:0,9:60:0");
        Assert.Equal(61, (MarketHttp.HeaderDeadline(response, now, 3) - now).TotalSeconds);
    }

    private sealed class Handler(bool duplicates) : HttpMessageHandler
    {
        public int Count,
            FetchCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        )
        {
            Count++;
            object payload;
            if (request.RequestUri!.AbsolutePath.Contains("/search/"))
                payload = new
                {
                    id = "q",
                    result = Enumerable.Range(0, 30).Select(i => i.ToString()).ToArray(),
                };
            else
            {
                var page = FetchCount++;
                payload = new
                {
                    result = Enumerable
                        .Range(page * 10, 10)
                        .Select(i => new
                        {
                            listing = new
                            {
                                account = new
                                {
                                    name = "seller" + (duplicates && page == 0 ? i % 5 : i),
                                    online = new { },
                                },
                                price = new { amount = i + 1, currency = "exalted" },
                            },
                            item = new
                            {
                                name = "Ring",
                                typeLine = "Ring",
                                explicitMods = Array.Empty<string>(),
                            },
                        })
                        .ToArray(),
                };
            }
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(payload),
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task FetchStopsOnlyAfterTenDistinctUsableSellers(
        bool duplicates,
        int expectedFetches
    )
    {
        var handler = new Handler(duplicates);
        using var http = new MarketHttp("https://example.invalid", handler);
        using var market = new TradeMarket(http);
        var item = new CatalogItem("ring", "Ring", "Ring", "unique", "", 1, 1, "", "", [], []);
        var result = await market.QuoteAsync(item, "L", null, null, default);
        Assert.Equal(10, result.Quote!.Samples);
        Assert.Equal(expectedFetches, handler.FetchCount);
        Assert.Equal(expectedFetches + 1, handler.Count);
        if (!duplicates)
            Assert.Equal(5.5m, result.Quote.UnitPrice);
    }
}
