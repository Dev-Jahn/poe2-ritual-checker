using OpenCvSharp;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class MouseUiTests
{
    private static string Data()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RitualChecker.sln")))
            root = root.Parent;
        return Path.Combine(root!.FullName, "data");
    }

    [Theory]
    [InlineData(1650, 800)]
    [InlineData(1640, 820)]
    public void SharperInventoryCellsCannotHideTheRitualGrid(int inventoryX, int inventoryY)
    {
        using var vision = new VisionEngine(Data());
        using var frame = new Mat(1440, 2560, MatType.CV_8UC3, Scalar.All(0));
        using var cell = Cv2.ImRead(Path.Combine(Data(), "ui", "empty-cell.png"));
        using var softer = new Mat();
        Cv2.GaussianBlur(cell, softer, new Size(3, 3), .6);
        void Paste(Mat pixels, int x, int y)
        {
            using var area = new Mat(frame, new Rect(x, y, pixels.Width, pixels.Height));
            pixels.CopyTo(area);
        }
        for (int y = 0; y < 5; y++)
        for (int x = 0; x < 12; x++)
            Paste(cell, inventoryX + 6 + x * 70, inventoryY + 4 + y * 70);
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 12; x++)
            Paste(softer, 106 + x * 70, 264 + y * 70);
        using var tribute = Cv2.ImRead(Path.Combine(Data(), "ui", "tribute-icon.png"));
        Paste(tribute, 380, 167);
        var grid = vision.DetectGrid(frame);
        Assert.NotNull(grid);
        Assert.InRange(grid.Bounds.X, 96, 104);
        Assert.InRange(grid.Bounds.Y, 256, 264);
        // Inventory remains visible after Ritual closes, but has no matching Ritual header.
        Cv2.Rectangle(frame, new Rect(0, 0, 1100, 1100), Scalar.All(0), -1);
        Assert.Null(vision.DetectGrid(frame));
    }

    [Theory]
    [InlineData(.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void MouseHoverRequiresGreenEdgesRatherThanGreenItemArt(double scale)
    {
        using var original = new Mat(120, 120, MatType.CV_8UC3, Scalar.All(0));
        var green = new Scalar(5, 65, 5);
        var box = new Box(
            (int)(20 * scale),
            (int)(20 * scale),
            (int)(70 * scale),
            (int)(70 * scale)
        );
        using var image = new Mat();
        Cv2.Rectangle(original, new Rect(28, 28, 54, 54), green, -1);
        Cv2.Resize(original, image, new Size(), scale, scale, InterpolationFlags.Nearest);
        Assert.False(VisionEngine.Selected(image, box, 70 * scale));
        Cv2.Line(original, new Point(20, 20), new Point(89, 20), green);
        Cv2.Resize(original, image, new Size(), scale, scale, InterpolationFlags.Nearest);
        Assert.False(VisionEngine.Selected(image, box, 70 * scale));
        Cv2.Line(original, new Point(20, 20), new Point(20, 89), green);
        Cv2.Resize(original, image, new Size(), scale, scale, InterpolationFlags.Nearest);
        Assert.True(VisionEngine.Selected(image, box, 70 * scale));
    }

    [Fact]
    public void CoveredColumnsDoNotMoveTheGridOrigin()
    {
        using var vision = new VisionEngine(Data());
        using var frame = new Mat(1440, 1800, MatType.CV_8UC3, Scalar.All(0));
        using var cell = Cv2.ImRead(Path.Combine(Data(), "ui", "empty-cell.png"));
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 12; x++)
        {
            if (x == 10)
                continue;
            using var area = new Mat(
                frame,
                new Rect(406 + x * 70, 264 + y * 70, cell.Width, cell.Height)
            );
            cell.CopyTo(area);
        }
        using var tribute = Cv2.ImRead(Path.Combine(Data(), "ui", "tribute-icon.png"));
        using (var area = new Mat(frame, new Rect(680, 167, tribute.Width, tribute.Height)))
            tribute.CopyTo(area);
        var grid = vision.DetectGrid(frame);
        Assert.NotNull(grid);
        Assert.InRange(grid.Bounds.X, 396, 404);
        Assert.InRange(grid.Bounds.Y, 256, 264);
    }

    [Fact]
    public void CompactTooltipAboveHoveredItemDoesNotMaskTheGrid()
    {
        using var frame = new Mat(1200, 1400, MatType.CV_8UC3, Scalar.All(0));
        var grid = new GridObservation(new Box(409, 356, 840, 700), 70, 1);
        Cv2.Rectangle(frame, new Rect(153, 58, 582, 42), new Scalar(70, 100, 120), 2);
        Assert.Null(VisionEngine.DetectTooltip(frame, grid));
        Cv2.Rectangle(frame, new Rect(409, 356, 70, 70), new Scalar(5, 65, 5), 1);
        var tooltip = VisionEngine.DetectTooltip(frame, grid);
        Assert.NotNull(tooltip);
        Assert.True(tooltip.X < 153 && tooltip.Y < 58);
        Assert.Equal(grid.Bounds.Y, tooltip.Bottom);
        Assert.False(tooltip.Intersects(grid.Bounds));
        Cv2.Rectangle(frame, new Rect(0, 0, 1400, 350), Scalar.All(0), -1);
        Assert.Null(VisionEngine.DetectTooltip(frame, grid));
    }

    [Theory]
    [InlineData("861", 861)]
    [InlineData("B61", 861)]
    [InlineData("I,OOO", 1000)]
    [InlineData("Q861", null)]
    [InlineData("B61junk", null)]
    [InlineData("8,61", null)]
    [InlineData("0", null)]
    [InlineData("-861", null)]
    public void TributeConsumesTheEntireNumericField(string text, int? expected) =>
        Assert.Equal(expected, TooltipParser.TributeAmount(text));
}
