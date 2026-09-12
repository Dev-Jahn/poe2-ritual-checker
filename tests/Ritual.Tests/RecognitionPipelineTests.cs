using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class RecognitionPipelineTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "RitualChecker.sln"))
        )
            directory = directory.Parent;
        return directory!.FullName;
    }

    [Theory]
    [InlineData("dextral-annulment-dim", "Omen_of_Dextral_Annulment", 1, 1, .75)]
    [InlineData("dextral-annulment-dim", "Omen_of_Dextral_Annulment", 1, 1, 1)]
    [InlineData("dextral-annulment-dim", "Omen_of_Dextral_Annulment", 1, 1, 1.5)]
    [InlineData("dextral-annulment-dim", "Omen_of_Dextral_Annulment", 1, 1, 2)]
    [InlineData("lavianga-cursor", "Laviangas_Spirits", 1, 2, .75)]
    [InlineData("lavianga-cursor", "Laviangas_Spirits", 1, 2, 1)]
    [InlineData("lavianga-cursor", "Laviangas_Spirits", 1, 2, 1.5)]
    [InlineData("lavianga-cursor", "Laviangas_Spirits", 1, 2, 2)]
    [InlineData("sacrosanctum-cursor", "Sacrosanctum", 2, 3, .75)]
    [InlineData("sacrosanctum-cursor", "Sacrosanctum", 2, 3, 1)]
    [InlineData("sacrosanctum-cursor", "Sacrosanctum", 2, 3, 1.5)]
    [InlineData("sacrosanctum-cursor", "Sacrosanctum", 2, 3, 2)]
    public void RealIconsKeepIdentityAndSizeWithCursorOcclusion(
        string fixture,
        string id,
        int width,
        int height,
        double scale
    )
    {
        using var vision = new VisionEngine(Path.Combine(Root(), "data"))
        {
            League = "Forbidden Rites",
        };
        using var source = Cv2.ImRead(Path.Combine(Root(), "tests", "fixtures", fixture + ".png"));
        var grid = new GridObservation(
            new(0, 0, (int)Math.Round(12 * 70.2142 * scale), (int)Math.Round(10 * 70.2142 * scale)),
            70.2142 * scale,
            1
        );
        using var frame = new Mat(
            grid.Bounds.Height,
            grid.Bounds.Width,
            MatType.CV_8UC3,
            Scalar.All(0)
        );
        var box = VisionEngine.CellBox(grid, 2, 3, width, height);
        using var crop = new Mat();
        Cv2.Resize(source, crop, new Size(box.Width, box.Height));
        using (var target = new Mat(frame, box.Rect()))
            crop.CopyTo(target);
        var result = vision.Analyze(frame, 1, grid);
        var item = Assert.Single(result.Items);
        Assert.Equal(id, item.CatalogId);
        Assert.Equal(width, item.Columns);
        Assert.Equal(height, item.Rows);
        Assert.Equal(1, item.Quantity);
    }

    [Theory]
    [InlineData(false, .95)]
    [InlineData(true, .95)]
    [InlineData(false, .60)]
    public void SameArtworkKeepsIdentityAcrossAmbiguousScoresButAReplacementDoesNot(
        bool replaced,
        double previousScore
    )
    {
        using var before = new Mat(280, 280, MatType.CV_8UC3);
        var random = new Random(17);
        for (int y = 0; y < 280; y++)
        for (int x = 0; x < 280; x++)
        {
            byte value = (byte)random.Next(35, 200);
            before.Set(y, x, new Vec3b(value, (byte)(value / 2), (byte)(value / 3)));
        }
        using var gray = new Mat();
        using var after = new Mat();
        Cv2.CvtColor(before, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(gray, after, ColorConversionCodes.GRAY2BGR);
        var items = Enumerable
            .Range(0, 4)
            .Select(x => new ItemObservation(
                $"{x}:0",
                new(x * 70, 0, 70, 70),
                x,
                0,
                1,
                1,
                $"item{x}",
                $"item{x}",
                "unique",
                1,
                previousScore,
                false,
                false,
                false,
                false,
                [new($"item{x}", $"item{x}", .95)]
            ))
            .ToArray();
        var old = new Analysis(1, "old", new(new(0, 0, 280, 280), 70, 1), items, null, [], 1);
        var changed = items[3] with
        {
            CatalogId = "wrong",
            Confidence = .65,
            Dimmed = true,
            Candidates = [new("wrong", "wrong", .65), new("item3", "item3", .64)],
        };
        if (replaced)
            using (var region = new Mat(after, changed.Bounds.Rect()))
                Cv2.Flip(region, region, FlipMode.X);
        var next = old with { Generation = 2, Items = [items[0], items[1], items[2], changed] };
        var result = RecognitionContinuity.Reconcile(before, old, after, next);
        Assert.Equal(replaced ? "wrong" : "item3", result.Analysis.Items[3].CatalogId);
        Assert.Equal(!replaced, Assert.Single(result.Conflicts).Retained);
    }

    [Fact]
    public void RealOmenAppearanceSurvivesGrayscaleAndDeferBadgeChanges()
    {
        using var sheet = Cv2.ImRead(
            Path.Combine(Root(), "tests", "fixtures", "dextral-annulment-transition.png")
        );
        using var before = new Mat(sheet, new Rect(0, 0, 71, 70));
        using var after = new Mat(sheet, new Rect(71, 0, 71, 70));
        Assert.True(
            RecognitionContinuity.AppearanceSimilarity(before, after, new(0, 0, 71, 70), true)
                >= .90
        );
    }

    [Fact]
    public void ANewGridNeverInheritsAnOldIdentity()
    {
        using var frame = new Mat(70, 70, MatType.CV_8UC3, Scalar.All(0));
        var old = new Analysis(1, "old", null, [], null, [], 1);
        var next = old with { Generation = 2, Grid = new(new(0, 0, 70, 70), 70, 1) };
        Assert.Same(next, RecognitionContinuity.Reconcile(frame, old, frame, next).Analysis);
    }

    [Fact]
    public void StrongLocalFragmentDoesNotSplitAnOtherwiseMatchingItem()
    {
        var regions = RegionPartitioner.Select(
            [new(0, 0, 1, 1, .84, 0), new(0, 1, 1, 1, .25, 1), new(0, 0, 1, 2, .80, 2)],
            1,
            2
        );
        Assert.Equal([2], regions);
    }

    [Fact]
    public void OneHighScoreCannotMergeSixWellSupportedItems()
    {
        var proposals = Enumerable
            .Range(0, 6)
            .Select(i => new RegionCandidate(i % 2, i / 2, 1, 1, .9, i))
            .ToList();
        proposals.Add(new(0, 0, 2, 3, .74, 6));
        Assert.Equal(Enumerable.Range(0, 6), RegionPartitioner.Select(proposals, 2, 3));
    }

    [Fact]
    public void PartitionPreservesAdjacentTallAndWideItems()
    {
        var proposals = new List<RegionCandidate>();
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            proposals.Add(new(x, y, 1, 1, .4, proposals.Count));
        proposals.Add(new(0, 0, 2, 4, .88, 16));
        proposals.Add(new(2, 0, 2, 3, .9, 17));
        proposals.Add(new(2, 3, 2, 1, .9, 18));
        Assert.Equal([16, 17, 18], RegionPartitioner.Select(proposals, 4, 4));
    }

    [Fact]
    public void AvailabilityIsScopedToLeagueAndIndependentOfPrices()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            JsonFiles.Write(
                path,
                new RitualPoolManifest(
                    1,
                    [new(["current"], ["removed"], "verified source", "drop disabled")]
                )
            );
            var pool = new RecognitionPool(path);
            Assert.Contains("removed", pool.Excluded("CURRENT"));
            Assert.Empty(pool.Excluded("Standard"));
            Assert.Empty(pool.Excluded(null));
            Assert.DoesNotContain("unpriced", pool.Excluded("current"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReportKeepsOriginalPixelsCandidatesAndManualCorrectionSeparate()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ritual-report-test-" + Guid.NewGuid());
        using var frame = new Mat(70, 70, MatType.CV_8UC3, new Scalar(15, 25, 55));
        using var previous = new Mat(70, 70, MatType.CV_8UC3, new Scalar(20, 30, 60));
        var analysis = new Analysis(7, "test", null, [], null, [], 2);
        var context = new RecognitionReportContext(
            "manual-correction",
            "test-session",
            "test-league",
            "0.4.0",
            "catalog",
            null,
            "0:0",
            "correct-item",
            analysis,
            analysis,
            null,
            [],
            [],
            [],
            []
        );
        string folder = await RecognitionReport.SaveAsync(directory, frame, context, previous);
        try
        {
            using var decoded = Cv2.ImRead(Path.Combine(folder, "capture.png"));
            Assert.Equal(0, Cv2.Norm(frame, decoded, NormTypes.INF));
            using var json = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(folder, "capture.json"))
            );
            var root = json.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("groundTruth").ValueKind);
            Assert.Equal(7, root.GetProperty("prediction").GetProperty("generation").GetInt32());
            Assert.Equal(
                "correct-item",
                root.GetProperty("report").GetProperty("correctedCatalogId").GetString()
            );
            Assert.Equal(
                Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "capture.png")))
                ),
                root.GetProperty("sha256").GetString()
            );
            Assert.True(File.Exists(Path.Combine(folder, "previous.png")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
