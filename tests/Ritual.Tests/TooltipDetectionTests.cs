using OpenCvSharp;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class TooltipDetectionTests
{
    [Theory]
    [InlineData(.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void OrangeItemContoursCannotHideTheGrid(double scale)
    {
        using var original = new Mat(1200, 1400, MatType.CV_8UC3, Scalar.All(0));
        var orange = new Scalar(20, 140, 230);
        // Short, disconnected item frames join after the colour mask is closed.
        for (int i = 0; i < 5; i++)
            Cv2.Rectangle(original, new Rect(104 + 70 * i, 424, 65, 60), orange, 2);
        using var frame = new Mat();
        Cv2.Resize(original, frame, new Size(), scale, scale, InterpolationFlags.Linear);
        var grid = Grid(scale);
        Assert.Null(VisionEngine.DetectTooltip(frame, grid));

        // A solid orange item silhouette has a closed outline but no dark title area.
        Cv2.Rectangle(original, new Rect(104, 424, 350, 60), orange, -1);
        Cv2.Resize(original, frame, new Size(), scale, scale, InterpolationFlags.Linear);
        Assert.Null(VisionEngine.DetectTooltip(frame, grid));
    }

    [Theory]
    [InlineData(.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void FramedTooltipCanStillOverlapLeftGridColumns(double scale)
    {
        using var original = new Mat(1200, 1400, MatType.CV_8UC3, Scalar.All(0));
        Cv2.Rectangle(original, new Rect(104, 424, 400, 60), new Scalar(20, 140, 230), 2);
        using var frame = new Mat();
        Cv2.Resize(original, frame, new Size(), scale, scale, InterpolationFlags.Linear);
        var tooltip = VisionEngine.DetectTooltip(frame, Grid(scale));
        Assert.NotNull(tooltip);
        Assert.True(tooltip.Intersects(Grid(scale).Bounds));
        Assert.InRange(tooltip.X, (int)(90 * scale), (int)(104 * scale));
        Assert.InRange(tooltip.Y, (int)(410 * scale), (int)(424 * scale));
    }

    private static GridObservation Grid(double scale) =>
        new(
            new((int)(100 * scale), (int)(260 * scale), (int)(840 * scale), (int)(700 * scale)),
            70 * scale,
            1
        );
}
