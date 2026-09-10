using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Ritual.Core;

public sealed record CapturedFrame(
    Mat Image,
    Box ScreenBounds,
    nint Window,
    string Backend,
    string ColorStatus
) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

public static class GameWindow
{
    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint window, ref POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left,
            Top,
            Right,
            Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X,
            Y;
    }

    public static bool IsGame(nint window)
    {
        if (window == 0)
            return false;
        GetWindowThreadProcessId(window, out var id);
        try
        {
            return Process
                .GetProcessById((int)id)
                .ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static Box Bounds(nint window)
    {
        if (!GetClientRect(window, out var rect))
            throw new InvalidOperationException("게임 창 크기를 읽지 못했습니다.");
        var point = new POINT();
        if (!ClientToScreen(window, ref point))
            throw new InvalidOperationException("게임 창 위치를 읽지 못했습니다.");
        return new(point.X, point.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
}

public static class GdiCapture
{
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(
        nint dc,
        ref BITMAPINFO info,
        uint usage,
        out nint bits,
        nint section,
        uint offset
    );

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        nint target,
        int x,
        int y,
        int width,
        int height,
        nint source,
        int sx,
        int sy,
        uint rop
    );

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint Size;
        public int Width,
            Height;
        public ushort Planes,
            BitCount;
        public uint Compression,
            SizeImage;
        public int XPelsPerMeter,
            YPelsPerMeter;
        public uint ClrUsed,
            ClrImportant;
    }

    public static CapturedFrame Capture(nint window)
    {
        if (!GameWindow.IsGame(window))
            throw new InvalidOperationException(
                "Path of Exile 2를 전면에 띄운 뒤 단축키를 누르세요."
            );
        var box = GameWindow.Bounds(window);
        if (box.Width < 100 || box.Height < 100)
            throw new InvalidOperationException("게임 창이 최소화되었습니다.");
        nint source = GetDC(0),
            dest = 0,
            bitmap = 0,
            old = 0;
        try
        {
            dest = CreateCompatibleDC(source);
            var info = new BITMAPINFO
            {
                Size = 40,
                Width = box.Width,
                Height = -box.Height,
                Planes = 1,
                BitCount = 32,
            };
            bitmap = CreateDIBSection(source, ref info, 0, out var bits, 0, 0);
            if (source == 0 || dest == 0 || bitmap == 0)
                throw new InvalidOperationException("화면 캡처 자원을 만들지 못했습니다.");
            old = SelectObject(dest, bitmap);
            if (!BitBlt(dest, 0, 0, box.Width, box.Height, source, box.X, box.Y, 0x40CC0020))
                throw new InvalidOperationException("화면 캡처 실패");
            using var bgra = Mat.FromPixelData(box.Height, box.Width, MatType.CV_8UC4, bits);
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return new(bgr, box, window, "GDI fallback", "SDR fallback · HDR 미검증");
        }
        finally
        {
            if (old != 0)
                SelectObject(dest, old);
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (dest != 0)
                DeleteDC(dest);
            if (source != 0)
                ReleaseDC(0, source);
        }
    }
}

public static class HdrToneMap
{
    // Linear scRGB uses 1.0 = 80 nits. SDR white is supplied by display metadata.
    public static byte Channel(float linear, float white = 1)
    {
        if (!float.IsFinite(linear) || white <= 0)
            return 0;
        float x = Math.Max(0, linear / white);
        float s = x <= .0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1 / 2.4f) - .055f;
        return (byte)Math.Clamp((int)MathF.Round(s * 255), 0, 255);
    }
}
