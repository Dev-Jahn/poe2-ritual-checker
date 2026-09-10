using System.Globalization;

namespace Ritual.Core;

public static class Valuation
{
    public static FormattedPrice Format(PriceQuote quote, int? quantity, Rate? rate)
    {
        if (quantity is null or <= 0)
            return new("수량 미확인", null, true);
        rate = quote.ExchangeRate ?? rate;
        var total = quote.UnitPrice * quantity.Value;
        decimal? exalted = quote.Currency switch
        {
            "exalted" => total,
            "divine" when rate?.ExaltedPerDivine > 0 => total * rate.ExaltedPerDivine,
            _ => null,
        };
        string text;
        if (exalted.HasValue && rate?.ExaltedPerDivine > 0)
            text =
                exalted.Value >= rate.ExaltedPerDivine
                    ? $"{Number(exalted.Value / rate.ExaltedPerDivine)} div"
                    : $"{Number(exalted.Value)} ex";
        else
            text =
                $"{Number(total)} {(quote.Currency == "exalted" ? "ex" : quote.Currency == "divine" ? "div" : quote.Currency)}";
        bool stale =
            quote.Stale
            || exalted.HasValue
                && rate is { ExaltedPerDivine: > 0 }
                && (
                    rate.Stale
                    || DateTimeOffset.UtcNow - rate.RetrievedAt > NinjaMarket.RefreshInterval
                );
        return new(
            (quote.Estimated ? "≈ " : "") + text + (stale ? "*" : ""),
            exalted,
            stale || quote.Estimated,
            stale
        );
    }

    private static string Number(decimal n) =>
        n is > 0 and < .01m
            ? "<0.01"
            : n.ToString(n >= 100 ? "0.#" : "0.##", CultureInfo.InvariantCulture);

    public static decimal? Efficiency(decimal? totalExalted, int? purchaseTribute, bool reliable) =>
        reliable && totalExalted >= 0 && purchaseTribute > 0
            ? totalExalted.Value / purchaseTribute.Value * 1000
            : null;

    public static decimal Median(IEnumerable<decimal> values)
    {
        var a = values.Order().ToArray();
        if (a.Length == 0)
            throw new ArgumentException("Empty price sample");
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }
}
