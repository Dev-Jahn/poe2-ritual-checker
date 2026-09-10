using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class RevisionTests
{
    [Fact]
    public void EmbeddingLoadsAgainstPublishedReferenceSnapshot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RitualChecker.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        using var vision = new VisionEngine(
            Path.Combine(root!.FullName, "data"),
            "hybrid",
            useUserExamples: false
        );
        Assert.NotEmpty(vision.Catalog.Items);
    }

    [Fact]
    public void OfficialCurrentArtRecognizesOmenCropsOnSyntheticGrid()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RitualChecker.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        using var vision = new VisionEngine(
            Path.Combine(root!.FullName, "data"),
            useUserExamples: false
        );
        using var frame = OpenCvSharp.Cv2.ImRead(
            Path.Combine(root.FullName, "tests", "fixtures", "omen-grid.png")
        );
        var result = vision.Analyze(frame, 1, new(new(0, 0, 840, 700), 70, 1));
        Assert.Equal(
            "Omen_of_Chaotic_Rarity",
            result.Items.Single(i => i.InstanceId == "0:1").CatalogId
        );
        Assert.Equal(
            "Omen_of_Chaotic_Rarity",
            result.Items.Single(i => i.InstanceId == "0:3").CatalogId
        );
        Assert.NotEqual(
            "Omen_of_Chaotic_Rarity",
            result.Items.Single(i => i.InstanceId == "0:5").CatalogId
        );
    }

    [Fact]
    public void FocusReturnCannotDisplayUntilNewFrameIsValidated()
    {
        var recovery = new FocusRecovery();
        Assert.True(recovery.CanDisplay(true));
        recovery.LostFocus();
        Assert.False(recovery.CanDisplay(false));
        Assert.False(recovery.CanDisplay(true));
        recovery.FrameValidated();
        Assert.True(recovery.CanDisplay(true));
        recovery.LostFocus();
        Assert.False(recovery.CanDisplay(true));
    }

    [Fact]
    public void WaitingIsNotUnavailable()
    {
        Assert.Equal("조회 대기…", Presentation.PendingPrice(true));
        Assert.Equal("리그 선택", Presentation.PendingPrice(false));
    }

    [Fact]
    public void UltrawidePreviewKeepsGridVisibleAtNativeAspect()
    {
        var grid = new Box(1300, 300, 840, 700);
        var crop = Presentation.Preview(grid, 70, 5120, 1440);
        Assert.True(
            crop.X <= grid.X
                && crop.Y <= grid.Y
                && crop.Right >= grid.Right
                && crop.Bottom >= grid.Bottom
        );
        Assert.True(crop.Width < 1200);
        Assert.True(crop.Right <= 5120);
    }

    [Fact]
    public void PreviewClampsAtScreenEdges()
    {
        var crop = Presentation.Preview(new(0, 0, 840, 700), 70, 850, 710);
        Assert.Equal(new Box(0, 0, 850, 710), crop);
    }

    [Fact]
    public void UncertainPricesDoNotGetExpensiveHighlight()
    {
        Assert.Null(Presentation.ValuePosition(1000, null, false));
        Assert.Null(Presentation.ValuePosition(1000, 100, true));
        Assert.True(
            Presentation.ValuePosition(1000, 100, false) > Presentation.ValuePosition(1, 100, false)
        );
    }

    [Fact]
    public void HoverNameWinsOverUnrelatedSelectionAndDuplicateNamesRemainUnassigned()
    {
        var known = new CatalogItem("a", "A", "A", "unique", "", 1, 1, "", "", [], []);
        var catalog = new Catalog("v", DateTimeOffset.UtcNow, [known]);
        ItemObservation Item(string id, string name, bool selected) =>
            new(
                id,
                new(0, 0, 70, 70),
                0,
                0,
                1,
                1,
                name,
                name,
                "unique",
                1,
                .99,
                false,
                false,
                false,
                selected,
                []
            );
        var tooltip = new TooltipInfo("a", "A", [], [], 1, null, false, true, "");
        var analysis = new Analysis(
            1,
            "",
            new(new(0, 0, 840, 700), 70, 1),
            [Item("0", "b", true), Item("1", "a", false)],
            null,
            [],
            0
        );
        Assert.Equal("1", TooltipBinding.Resolve(analysis, tooltip, catalog)!.InstanceId);
        Assert.Null(
            TooltipBinding.Resolve(
                analysis with
                {
                    Items = [Item("0", "a", false), Item("1", "a", false)],
                },
                tooltip,
                catalog
            )
        );
        Assert.Null(
            TooltipBinding.Resolve(
                analysis with
                {
                    Items = [Item("0", "b", true)],
                },
                tooltip,
                catalog
            )
        );
    }
}
