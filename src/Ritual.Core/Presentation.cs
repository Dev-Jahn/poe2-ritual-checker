namespace Ritual.Core;

public static class Presentation
{
    public static string PendingPrice(bool leagueSelected) =>
        leagueSelected ? "조회 대기…" : "리그 선택";

    public static Box Preview(Box? grid, double cell, int width, int height)
    {
        if (grid is null)
            return new(0, 0, width, height);
        int pad = (int)cell,
            left = Math.Max(0, grid.X - pad / 2),
            top = Math.Max(0, grid.Y - pad * 3);
        return new(
            left,
            top,
            Math.Min(width - left, grid.Width + pad),
            Math.Min(height - top, grid.Bottom + pad - top)
        );
    }

    // A missing exchange rate never creates a fictional value tier.
    public static double? ValuePosition(decimal? exalted, decimal? rate, bool estimated)
    {
        if (estimated || exalted is null || rate is null or <= 0)
            return null;
        return Math.Clamp((Math.Log10(Math.Max(.001, (double)(exalted / rate))) + 2) / 3, 0, 1);
    }
}
