namespace Ritual.Core;

public static class AnalysisContinuity
{
    public static bool SameItems(Analysis? previous, Analysis next)
    {
        if (
            previous?.Grid is null
            || next.Grid is null
            || previous.Grid.Bounds != next.Grid.Bounds
            || previous.Items.Length == 0
            || previous.Items.Length != next.Items.Length
        )
            return false;
        var old = previous.Items.ToDictionary(i => i.InstanceId);
        return next.Items.All(item =>
            item.CatalogId is not null
            && old.TryGetValue(item.InstanceId, out var prior)
            && item.CatalogId == prior.CatalogId
            && item.Bounds == prior.Bounds
            && item.Quantity == prior.Quantity
        );
    }
}
