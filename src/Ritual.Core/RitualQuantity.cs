namespace Ritual.Core;

public static class RitualQuantity
{
    // Omens are offered singly in Ritual even though inventory stacks can hold more.
    public static bool IsSingle(CatalogItem item) =>
        item.Kind == "unique"
        || item.MaxStackSize == 1
        || (item.Kind == "currency" && item.Id.StartsWith("Omen_of_", StringComparison.Ordinal));

    public static int? ForIdentification(CatalogItem item, ItemObservation? previous = null) =>
        IsSingle(item) ? 1
        : previous?.CatalogId == item.Id ? previous.Quantity
        : null;
}
