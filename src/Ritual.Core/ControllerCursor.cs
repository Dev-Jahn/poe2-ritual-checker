using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Ritual.Core;

public static class ControllerCursor
{
    public static Box[] Detect(Mat frame, GridObservation grid)
    {
        using var region = new Mat(frame, grid.Bounds.Rect());
        using var normalized = new Mat();
        Cv2.Resize(region, normalized, new Size(840, 700));
        using var bright = new Mat();
        Cv2.InRange(normalized, new Scalar(46, 131, 151), Scalar.All(255), bright);
        var pixels = new byte[840 * 700];
        Marshal.Copy(bright.Data, pixels, 0, pixels.Length);
        bool Near(int x, int y, int radius = 1)
        {
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                if (
                    x + dx >= 0
                    && x + dx < 840
                    && y + dy >= 0
                    && y + dy < 700
                    && pixels[(y + dy) * 840 + x + dx] != 0
                )
                    return true;
            return false;
        }
        var peaks = new List<Point>();
        for (int y = 8; y < 692; y += 2)
        for (int x = 33; x < 807; x += 2)
        {
            if (
                pixels[y * 840 + x] == 0
                || peaks.Any(p => Math.Abs(p.X - x) < 20 && Math.Abs(p.Y - y) < 20)
            )
                continue;
            if (!Near(x - 4, y + 3) || !Near(x + 4, y + 3) || Near(x - 9, y) || Near(x + 9, y))
                continue;
            int Left = 0,
                Right = 0;
            foreach (int dy in new[] { -32, -18, -3 })
            {
                if (Near(x - 33, y + dy, 2))
                    Left++;
                if (Near(x + 33, y + dy, 2))
                    Right++;
            }
            // The chevron plus both square sides distinguish focus from item artwork.
            if (Left >= 2 && Right >= 2)
                peaks.Add(new(x, y));
        }
        double scale = grid.CellSize / 70;
        return peaks
            .Select(p => new Box(
                grid.Bounds.X + (int)Math.Round((p.X - 35) * scale),
                grid.Bounds.Y + (int)Math.Round((p.Y - 62) * scale),
                (int)Math.Round(grid.CellSize),
                (int)Math.Round(grid.CellSize)
            ))
            .ToArray();
    }
}
