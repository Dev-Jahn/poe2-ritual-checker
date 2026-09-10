namespace Ritual.Core;

public static class TooltipBinding
{
    public static bool Fits(ItemObservation observed, CatalogItem item) =>
        (
            observed.Columns == item.Width
            || (
                item.Width == 1
                && observed.Columns == 2
                && item.Height >= 3
                && item.ImageSource.Contains("/Weapons/")
            )
        )
        && (
            observed.Rows == item.Height
            || (
                item.Width == 2
                && item.Height == 2
                && observed.Rows == 3
                && item.ImageSource.Contains("/Foci/")
            )
        );

    public static ItemObservation? Resolve(Analysis analysis, TooltipInfo tooltip, Catalog catalog)
    {
        var known = catalog.Items.SingleOrDefault(i => i.Id == tooltip.CatalogId);
        if (known is null)
            return null;
        var named = analysis.Items.Where(i => i.CatalogId == known.Id && Fits(i, known)).ToArray();
        if (named.Length == 1)
            return named[0];
        if (named.Length > 1)
        {
            var selected = named.Where(i => i.Selected).ToArray();
            return selected.Length == 1 ? selected[0] : null;
        }
        var uncertain = analysis
            .Items.Where(i => i.Selected && i.Estimated && Fits(i, known))
            .ToArray();
        return uncertain.Length == 1 ? uncertain[0] : null;
    }
}
