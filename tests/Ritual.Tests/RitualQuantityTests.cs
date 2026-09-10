using OpenCvSharp;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class RitualQuantityTests
{
    private static CatalogItem Item(string id, string kind = "currency", int? maxStack = 10) =>
        new(id, id, id, kind, "", 1, 1, "", "", [], [], maxStack);

    private static ItemObservation Observation(string id, int? quantity = null) =>
        new(
            "0:0",
            new(0, 0, 70, 70),
            0,
            0,
            1,
            1,
            id,
            id,
            "currency",
            quantity,
            .6,
            true,
            true,
            true,
            false,
            []
        );

    [Fact]
    public async Task OmenPriceDoesNotDependOnVisibleDigitsOrOcr()
    {
        var item = Item("Omen_of_Bartering");
        var reader = new TextReaderEngine();
        reader.SingleQuantityItems.Add(item.Id);
        // No pixels: reading the image would fail. Known singles must skip OCR entirely.
        using var frame = new Mat();
        var analysis = new Analysis(
            1,
            "test",
            new(new(0, 0, 840, 700), 70, 1),
            [Observation(item.Id)],
            null,
            [],
            0,
            true
        );
        var result = await reader.ReadQuantitiesAsync(frame, analysis, default);
        var observed = Assert.Single(result.Items);
        Assert.Equal(1, observed.Quantity);
        Assert.True(observed.Estimated);
        Assert.True(observed.Dimmed);
        Assert.True(observed.Deferred);
        var price = new PriceQuote(
            item.Id,
            "league",
            9,
            "exalted",
            DateTimeOffset.UtcNow,
            "test",
            "test",
            1
        );
        Assert.Equal("9 ex", Valuation.Format(price, observed.Quantity, null).Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(7)]
    public async Task FixedQuantityCannotBeOverwrittenByMisreadDigits(int? prior)
    {
        var reader = new TextReaderEngine();
        reader.SingleQuantityItems.Add("Omen_of_Bartering");
        using var frame = new Mat();
        var result = await reader.ReadQuantitiesAsync(
            frame,
            new(
                1,
                "test",
                new(new(0, 0, 840, 700), 70, 1),
                [Observation("Omen_of_Bartering", prior)],
                null,
                [],
                0
            ),
            default
        );
        Assert.Equal(1, result.Items[0].Quantity);
    }

    [Fact]
    public void InventoryStackLimitAndOtherCurrencyQuantitiesRemainDistinct()
    {
        var omen = Item("Omen_of_Bartering", maxStack: 10);
        Assert.Equal(1, RitualQuantity.ForIdentification(omen));
        Assert.Equal(10, omen.MaxStackSize);
        var exalted = Item("Exalted_Orb", maxStack: 20);
        Assert.Null(RitualQuantity.ForIdentification(exalted));
        Assert.Equal(12, RitualQuantity.ForIdentification(exalted, Observation(exalted.Id, 12)));
        Assert.Equal(1, RitualQuantity.ForIdentification(Item("Sacred_Bloom", maxStack: 1)));
        Assert.Equal(1, RitualQuantity.ForIdentification(Item("Chober_Chaber", "unique", null)));
    }

    [Fact]
    public void CorrectingAnOmenToAnotherCurrencyDoesNotReuseItsAssumedOne()
    {
        var omen = Item("Omen_of_Bartering");
        var orb = Item("Exalted_Orb");
        Assert.Null(RitualQuantity.ForIdentification(orb, Observation(omen.Id, 1)));
        Assert.Equal(1, RitualQuantity.ForIdentification(omen, Observation(orb.Id)));
    }
}
