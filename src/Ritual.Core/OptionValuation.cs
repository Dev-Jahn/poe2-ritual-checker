using System.Globalization;
using System.Text.RegularExpressions;

namespace Ritual.Core;

public record RollInterval(double Min, double Max);

public record ExtremeRoll(string ModKey, double Value, double Min, double Max);

public static class OptionValuation
{
    private const string Number = @"[+−-]?\d+(?:\.\d+)?";
    private static readonly Regex Tokens = new(
        @"\(\s*(?<low>" + Number + @")\s*[—–−-]\s*(?<high>" + Number + @")\s*\)|" + Number,
        RegexOptions.Compiled
    );

    private static double Parse(string value) =>
        double.Parse(value.Replace('−', '-'), CultureInfo.InvariantCulture);

    public static RollInterval[] Intervals(string template) =>
        Tokens
            .Matches(template)
            .Select(m =>
                m.Groups["low"].Success
                    ? new RollInterval(
                        Math.Min(Parse(m.Groups["low"].Value), Parse(m.Groups["high"].Value)),
                        Math.Max(Parse(m.Groups["low"].Value), Parse(m.Groups["high"].Value))
                    )
                    : new RollInterval(Parse(m.Value), Parse(m.Value))
            )
            .ToArray();

    public static bool Similar(
        string[] listingMods,
        Dictionary<string, double[]> target,
        CatalogItem item
    )
    {
        var observed = listingMods
            .GroupBy(TooltipParser.ModKey)
            .ToDictionary(g => g.Key, g => g.ToArray());
        if (observed.Count != target.Count)
            return false;
        foreach (var (key, values) in target)
        {
            var templates = item.ModsEn.Where(t => TooltipParser.ModKey(t) == key).ToArray();
            if (
                templates.Length != 1
                || !observed.TryGetValue(key, out var lines)
                || lines.Length != 1
            )
                return false;
            var intervals = Intervals(templates[0]);
            var actual = TooltipParser.Values(lines[0]);
            if (intervals.Length != values.Length || actual.Length != values.Length)
                return false;
            for (int i = 0; i < values.Length; i++)
            {
                var range = intervals[i];
                double tolerance = (range.Max - range.Min) * .10;
                if (
                    values[i] < range.Min
                    || values[i] > range.Max
                    || actual[i] < range.Min
                    || actual[i] > range.Max
                    || Math.Abs(actual[i] - values[i]) > tolerance + 1e-8
                )
                    return false;
            }
        }
        return true;
    }

    // Either boundary can be valuable. Direction and price premium are never inferred
    // merely from a high number. This selects an investigation, not a valuation.
    public static ExtremeRoll[] ExtremeConditions(CatalogItem item, TooltipInfo? tooltip)
    {
        if (tooltip is not { Complete: true } || tooltip.Mods.Count == 0)
            return [];
        var result = new List<ExtremeRoll>();
        foreach (var template in item.ModsEn)
        {
            var key = TooltipParser.ModKey(template);
            if (!tooltip.Mods.TryGetValue(key, out var values))
                return [];
            var intervals = Intervals(template);
            if (intervals.Length != values.Length)
                return [];
            for (int i = 0; i < values.Length; i++)
                if (values[i] < intervals[i].Min || values[i] > intervals[i].Max)
                    return [];
            // Multi-value stats (e.g. damage intervals) need an explicit mapping;
            // do not guess the trade API's aggregation semantics.
            if (intervals.Length != 1 || intervals[0].Min == intervals[0].Max)
                continue;
            var r = intervals[0];
            double fraction = (values[0] - r.Min) / (r.Max - r.Min);
            if (fraction <= .05 || fraction >= .95)
                result.Add(
                    new(
                        key,
                        values[0],
                        fraction <= .05 ? r.Min : r.Max - (r.Max - r.Min) * .10,
                        fraction <= .05 ? r.Min + (r.Max - r.Min) * .10 : r.Max
                    )
                );
        }
        return result.ToArray();
    }

    public static string ProbeKey(string league, CatalogItem item, TooltipInfo tooltip) =>
        league
        + "|"
        + item.Id
        + "|"
        + tooltip.Corrupted
        + "|extreme-v2|"
        + string.Join(
            ";",
            ExtremeConditions(item, tooltip)
                .OrderBy(r => r.ModKey)
                .Select(r =>
                    r.ModKey
                    + ":"
                    + r.Min.ToString("R", CultureInfo.InvariantCulture)
                    + ":"
                    + r.Max.ToString("R", CultureInfo.InvariantCulture)
                )
        );
}
