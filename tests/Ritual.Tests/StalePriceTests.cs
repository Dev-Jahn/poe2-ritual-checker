using System.Globalization;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class StalePriceTests
{
    [Theory]
    [InlineData("0.05", 1, "12.5 ex*")]
    [InlineData("0.999999", 1, "250 ex*")]
    [InlineData("0.5", 2, "1 div*")]
    [InlineData("0.5", 3, "1.5 div*")]
    [InlineData("0.00001", 1, "<0.01 ex*")]
    public void OldPricesUseTheirSavedRateAndUnitBoundary(
        string amount,
        int quantity,
        string expected
    )
    {
        var old = DateTimeOffset.UtcNow.AddDays(-7);
        var quote = new PriceQuote(
            "item",
            "league",
            decimal.Parse(amount, CultureInfo.InvariantCulture),
            "divine",
            old,
            "poe.ninja",
            "representative",
            10,
            Stale: true,
            ExchangeRate: new(250, old, Stale: true)
        );
        var shown = Valuation.Format(quote, quantity, new(500, DateTimeOffset.UtcNow));
        Assert.Equal(expected, shown.Text);
        Assert.True(shown.Stale);
        Assert.DoesNotContain("오래됨", shown.Text);
    }

    [Fact]
    public void OldExchangeRateAloneAddsAsteriskWithoutChangingThePriceUnit()
    {
        var quote = new PriceQuote(
            "item",
            "league",
            .05m,
            "divine",
            DateTimeOffset.UtcNow,
            "trade",
            "options",
            10
        );
        var shown = Valuation.Format(quote, 1, new(250, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal("12.5 ex*", shown.Text);
        Assert.True(shown.Stale);
    }

    [Fact]
    public void NeverObservedExchangeRateDoesNotInventExaltsOrRoundSmallPositivePriceToZero()
    {
        var quote = new PriceQuote(
            "item",
            "league",
            .001m,
            "divine",
            DateTimeOffset.UtcNow,
            "test",
            "test",
            1,
            Stale: true
        );
        var shown = Valuation.Format(quote, 1, null);
        Assert.Equal("<0.01 div*", shown.Text);
        Assert.Null(shown.TotalExalted);
    }
}
