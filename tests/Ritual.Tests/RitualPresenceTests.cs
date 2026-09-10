using OpenCvSharp;
using Ritual.Core;
using Xunit;

namespace Ritual.Tests;

public class RitualPresenceTests
{
    private static string Data()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RitualChecker.sln")))
            root = root.Parent;
        return Path.Combine(root!.FullName, "data");
    }

    private static void Paste(Mat image, Mat patch, int x, int y)
    {
        using var region = new Mat(image, new Rect(x, y, patch.Width, patch.Height));
        patch.CopyTo(region);
    }

    [Theory]
    [InlineData(.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void DeferSelectionSurvivesCoveredHeaderAndThickItemBorder(double scale)
    {
        string data = Data();
        using var vision = new VisionEngine(data);
        using var original = new Mat(1100, 1100, MatType.CV_8UC3, Scalar.All(0));
        using var cell = Cv2.ImRead(Path.Combine(data, "ui", "empty-cell.png"));
        using var icon = Cv2.ImRead(Path.Combine(data, "ui", "defer-mode-icon.png"));
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 12; x++)
            Paste(original, cell, 106 + x * 70, 274 + y * 70);
        Paste(original, icon, 820, 180);
        Cv2.Rectangle(original, new Rect(100, 270, 140, 210), new Scalar(20, 225, 255), 9);
        using var scaled = new Mat();
        Cv2.Resize(original, scaled, new Size(), scale, scale);
        var grid = new GridObservation(
            new Box((int)(100 * scale), (int)(270 * scale), (int)(840 * scale), (int)(700 * scale)),
            70 * scale,
            1
        );
        Assert.True(vision.VerifyRitualWindow(scaled, grid));
        // The same grid without the defer symbol could be a different inventory window.
        Cv2.Rectangle(original, new Rect(800, 165, 91, 98), Scalar.All(0), -1);
        Cv2.Resize(original, scaled, new Size(), scale, scale);
        Assert.False(vision.VerifyRitualWindow(scaled, grid));
        original.SetTo(Scalar.All(0));
        Paste(original, icon, 820, 180);
        Cv2.Resize(original, scaled, new Size(), scale, scale);
        Assert.False(vision.VerifyRitualWindow(scaled, grid));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public void TributeIconMayMoveAcrossTheCostLine(int column)
    {
        string data = Data();
        using var vision = new VisionEngine(data);
        using var image = new Mat(1100, 1100, MatType.CV_8UC3, Scalar.All(0));
        using var tribute = Cv2.ImRead(Path.Combine(data, "ui", "tribute-icon.png"));
        var grid = new GridObservation(new Box(100, 270, 840, 700), 70, 1);
        Paste(image, tribute, 100 + column * 70, 177);
        Assert.True(vision.VerifyRitualWindow(image, grid));
        image.SetTo(Scalar.All(0));
        Assert.False(vision.VerifyRitualWindow(image, grid));
    }

    [Theory]
    [InlineData(.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void DeferFailureKeepsSessionAliveWithEveryHeaderAnchorCovered(double scale)
    {
        string data = Data();
        using var vision = new VisionEngine(data);
        using var original = new Mat(1200, 1100, MatType.CV_8UC3, Scalar.All(0));
        using var cell = Cv2.ImRead(Path.Combine(data, "ui", "empty-cell.png"));
        using var label = Cv2.ImRead(Path.Combine(data, "ui", "defer-action-label.png"));
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 12; x++)
            Paste(original, cell, 106 + x * 70, 274 + y * 70);
        Paste(original, label, 473, 1081);
        var grid = new GridObservation(
            new Box((int)(100 * scale), (int)(270 * scale), (int)(840 * scale), (int)(700 * scale)),
            70 * scale,
            1
        );
        using var scaled = new Mat();
        Cv2.Resize(original, scaled, new Size(), scale, scale);
        Assert.True(vision.IsDeferMode(scaled, grid));
        var presence = new RitualPresence();
        // A persistent notification must not accumulate misses and stop capture.
        for (int frame = 0; frame < 4; frame++)
        {
            bool visible = vision.VerifyRitualWindow(scaled, grid);
            Assert.True(visible);
            Assert.False(presence.Observe(visible));
        }
        // The action label alone does not prove that the Ritual grid is still open.
        original.SetTo(Scalar.All(0));
        Paste(original, label, 473, 1081);
        Cv2.Resize(original, scaled, new Size(), scale, scale);
        Assert.False(vision.VerifyRitualWindow(scaled, grid));
        Assert.False(presence.Observe(false));
        Assert.True(presence.Observe(false));
        // Grid texture alone remains insufficient, and must not imply defer mode.
        original.SetTo(Scalar.All(0));
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 12; x++)
            Paste(original, cell, 106 + x * 70, 274 + y * 70);
        Cv2.Resize(original, scaled, new Size(), scale, scale);
        Assert.False(vision.VerifyRitualWindow(scaled, grid));
        Assert.False(vision.IsDeferMode(scaled, grid));
    }

    [Fact]
    public void TransitionMissDoesNotCloseAndAValidFrameResetsTheCount()
    {
        var presence = new RitualPresence();
        Assert.False(presence.Observe(true));
        Assert.False(presence.Observe(false));
        Assert.False(presence.Observe(true));
        Assert.False(presence.Observe(false));
        Assert.True(presence.Observe(false));
        presence.Reset();
        Assert.False(presence.Observe(false));
    }
}
