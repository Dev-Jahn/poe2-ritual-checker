using System.Text.Json;
using OpenCvSharp;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class ControllerRecognitionTests
{
    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RitualChecker.sln")))
            root = root.Parent;
        return root!.FullName;
    }

    [Theory]
    [InlineData(.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void DimmedSinistralExaltationSurvivesControllerGlow(double scale)
    {
        using var vision = new VisionEngine(Path.Combine(Root(), "data"));
        using var original = Cv2.ImRead(
            Path.Combine(Root(), "tests/fixtures/sinistral-exaltation-selected.png")
        );
        using var crop = new Mat();
        double cell = 70.215 * scale;
        var grid = new GridObservation(
            new(0, 0, (int)Math.Round(cell * 12), (int)Math.Round(cell * 10)),
            cell,
            1
        );
        var box = VisionEngine.CellBox(grid, 2, 3, 1, 1);
        Cv2.Resize(
            original,
            crop,
            new Size(box.Width, box.Height),
            0,
            0,
            InterpolationFlags.Linear
        );
        using var frame = new Mat(
            grid.Bounds.Height,
            grid.Bounds.Width,
            MatType.CV_8UC3,
            Scalar.All(0)
        );
        using (var target = new Mat(frame, new Rect(box.X, box.Y, box.Width, box.Height)))
            crop.CopyTo(target);
        var result = vision.Analyze(frame, 1, grid);
        var observed = Assert.Single(result.Items);
        Assert.True(observed.Selected);
        Assert.Equal("Omen_of_Sinistral_Exaltation", observed.CatalogId);
        Assert.Equal(1, observed.Quantity);
    }

    [Theory]
    [InlineData("Omen_of_Sinistral_Exaltation")]
    [InlineData("Omen_of_Sinistral_Erasure")]
    [InlineData("Omen_of_Sinistral_Annulment")]
    [InlineData("Omen_of_Dextral_Exaltation")]
    [InlineData("Omen_of_Dextral_Erasure")]
    [InlineData("Omen_of_Dextral_Annulment")]
    public void SelectionDoesNotFavourOneOmenVariant(string id)
    {
        string data = Path.Combine(Root(), "data");
        using var vision = new VisionEngine(data, useUserExamples: false);
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(data, "official-references.json"))
        );
        var relative = manifest
            .RootElement.GetProperty("items")
            .EnumerateArray()
            .Single(i => i.GetProperty("catalogId").GetString() == id)
            .GetProperty("image")
            .GetString()!;
        using var rgba = Cv2.ImRead(Path.Combine(data, relative), ImreadModes.Unchanged);
        using var resized = new Mat();
        Cv2.Resize(rgba, resized, new Size(70, 70));
        using var original = new Mat(70, 70, MatType.CV_8UC3);
        for (int y = 0; y < 70; y++)
        for (int x = 0; x < 70; x++)
        {
            var p = resized.At<Vec4b>(y, x);
            float alpha = p.Item3 / 255f;
            original.Set(
                y,
                x,
                new Vec3b((byte)(p.Item0 * alpha), (byte)(p.Item1 * alpha), (byte)(p.Item2 * alpha))
            );
        }
        foreach (bool dimmed in new[] { false, true })
        foreach (bool selected in new[] { false, true })
        {
            using var crop = original.Clone();
            if (dimmed)
            {
                using var gray = new Mat();
                Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
                gray.ConvertTo(gray, -1, .4);
                Cv2.CvtColor(gray, crop, ColorConversionCodes.GRAY2BGR);
            }
            // The game's blue slot background stays coloured when the icon is dimmed.
            for (int y = 0; y < 70; y++)
            for (int x = 0; x < 70; x++)
            {
                float background = 1 - resized.At<Vec4b>(y, x).Item3 / 255f;
                var p = crop.At<Vec3b>(y, x);
                crop.Set(
                    y,
                    x,
                    new Vec3b(
                        (byte)(p.Item0 + 24 * background),
                        (byte)(p.Item1 + 2 * background),
                        (byte)(p.Item2 + 2 * background)
                    )
                );
            }
            if (selected)
            {
                var yellow = new Scalar(100, 220, 240);
                Cv2.Line(crop, new Point(0, 69), new Point(25, 69), yellow, 2);
                Cv2.Line(crop, new Point(25, 69), new Point(35, 61), yellow, 2);
                Cv2.Line(crop, new Point(35, 61), new Point(45, 69), yellow, 2);
                Cv2.Line(crop, new Point(45, 69), new Point(69, 69), yellow, 2);
            }
            using var frame = new Mat(700, 840, MatType.CV_8UC3, Scalar.All(0));
            using (var target = new Mat(frame, new Rect(140, 210, 70, 70)))
                crop.CopyTo(target);
            var result = vision.Analyze(frame, 1, new(new(0, 0, 840, 700), 70, 1));
            var observed = Assert.Single(result.Items);
            Assert.Equal(selected, observed.Selected);
            Assert.Equal(id, observed.CatalogId);
        }
    }
}
