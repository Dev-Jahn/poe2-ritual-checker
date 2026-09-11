using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class AnalysisContinuityTests
{
    private static Analysis Screen() =>
        new(
            1,
            "normal",
            new(new(100, 250, 840, 700), 70, 1),
            [
                new(
                    "0:0",
                    new(100, 250, 70, 70),
                    0,
                    0,
                    1,
                    1,
                    "Omen_of_Bartering",
                    "징조",
                    "currency",
                    1,
                    .95,
                    false,
                    false,
                    false,
                    false,
                    []
                ),
                new(
                    "1:0",
                    new(170, 250, 140, 210),
                    1,
                    0,
                    2,
                    3,
                    "armour",
                    "갑옷",
                    "unique",
                    1,
                    .91,
                    false,
                    false,
                    false,
                    false,
                    []
                ),
            ],
            null,
            [],
            10
        );

    [Fact]
    public void ModeAndDisplayStateChangesKeepTheSameItemPrices()
    {
        var before = Screen();
        var after = before with
        {
            Generation = 2,
            Fingerprint = "defer",
            DeferMode = true,
            TooltipBounds = new(950, 250, 500, 500),
            Items = before
                .Items.Reverse()
                .Select(i =>
                    i with
                    {
                        Selected = true,
                        Deferred = true,
                        Dimmed = true,
                        Confidence = .7,
                        Estimated = true,
                    }
                )
                .ToArray(),
        };
        Assert.True(AnalysisContinuity.SameItems(before, after));
    }

    [Theory]
    [InlineData("purchase")]
    [InlineData("replacement")]
    [InlineData("quantity")]
    [InlineData("position")]
    [InlineData("grid")]
    [InlineData("closed")]
    [InlineData("unknown")]
    public void ChangedContentsCannotInheritOldOptionPrices(string change)
    {
        var before = Screen();
        var after = change switch
        {
            "purchase" => before with { Items = before.Items.Take(1).ToArray() },
            "replacement" => before with
            {
                Items = [before.Items[0] with { CatalogId = "other" }, before.Items[1]],
            },
            "quantity" => before with
            {
                Items = [before.Items[0] with { Quantity = 2 }, before.Items[1]],
            },
            "position" => before with
            {
                Items = [before.Items[0] with { Bounds = new(200, 250, 70, 70) }, before.Items[1]],
            },
            "grid" => before with { Grid = new(new(200, 250, 840, 700), 70, 1) },
            "closed" => before with { Grid = null, Items = [] },
            _ => before with
            {
                Items = [before.Items[0] with { CatalogId = null }, before.Items[1]],
            },
        };
        Assert.False(AnalysisContinuity.SameItems(before, after));
    }
}
