using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Ritual.Core;

namespace Ritual.App;

public record PriceLabel(
    ItemObservation Item,
    string Text,
    string Detail,
    decimal? Exalted = null,
    decimal? ExaltedPerDivine = null
);

public sealed class LabelsLayer : FrameworkElement
{
    public PriceLabel[] Labels { get; set; } = [];
    public Box? Tooltip { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public bool DrawBoxes { get; set; }

    private static Brush ValueBrush(PriceLabel label)
    {
        var position = Presentation.ValuePosition(label.Exalted, label.ExaltedPerDivine);
        if (position is null)
            return Brushes.LightSlateGray;
        var stops = new[]
        {
            Color.FromRgb(218, 229, 241),
            Color.FromRgb(93, 240, 193),
            Color.FromRgb(255, 217, 92),
            Color.FromRgb(255, 117, 193),
        };
        double scaled = position.Value * 3;
        int i = Math.Min(2, (int)scaled);
        double mix = scaled - i;
        return new SolidColorBrush(
            Color.FromRgb(
                (byte)(stops[i].R * (1 - mix) + stops[i + 1].R * mix),
                (byte)(stops[i].G * (1 - mix) + stops[i + 1].G * mix),
                (byte)(stops[i].B * (1 - mix) + stops[i + 1].B * mix)
            )
        );
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var label in Labels)
        {
            var b = label.Item.Bounds;
            var rect = new Rect(
                (b.X - OffsetX) * ScaleX,
                (b.Y - OffsetY) * ScaleY,
                b.Width * ScaleX,
                b.Height * ScaleY
            );
            if (DrawBoxes)
                dc.DrawRectangle(
                    null,
                    new Pen(
                        label.Item.Estimated ? Brushes.DarkOrange : Brushes.MediumAquamarine,
                        1
                    ),
                    rect
                );
            var text = new FormattedText(
                (label.Item.Estimated ? "~ " : "") + label.Text,
                CultureInfo.GetCultureInfo("ko-KR"),
                FlowDirection.LeftToRight,
                new Typeface("Malgun Gothic"),
                Math.Clamp(13 * ScaleX, 11, 18),
                ValueBrush(label),
                dpi
            );
            text.MaxTextWidth = Math.Max(24, rect.Width - 6);
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
            var tag = new Rect(
                rect.Left + 1,
                rect.Bottom - text.Height - 4,
                text.Width + 6,
                text.Height + 4
            );
            if (
                Tooltip is { } t
                && new Rect(
                    (t.X - OffsetX) * ScaleX,
                    (t.Y - OffsetY) * ScaleY,
                    t.Width * ScaleX,
                    t.Height * ScaleY
                ).IntersectsWith(tag)
            )
                continue;
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(242, 8, 11, 16)),
                new Pen(ValueBrush(label), .6),
                tag,
                3,
                3
            );
            dc.DrawText(text, new Point(tag.X + 3, tag.Y + 2));
        }
    }
}

public sealed class Overlay : Window
{
    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint after,
        int x,
        int y,
        int w,
        int h,
        uint flags
    );

    private readonly LabelsLayer layer = new();

    public Overlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        Content = layer;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongPtrW(hwnd, -20, GetWindowLongPtrW(hwnd, -20) | 0x08000000 | 0x20 | 0x80);
            SetWindowDisplayAffinity(hwnd, 0x11);
        };
    }

    public void Update(Box bounds, PriceLabel[] labels, Box? tooltip)
    {
        if (!IsVisible)
            Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10);
        var dpi = VisualTreeHelper.GetDpi(this);
        layer.ScaleX = 1 / dpi.DpiScaleX;
        layer.ScaleY = 1 / dpi.DpiScaleY;
        layer.Labels = labels;
        layer.Tooltip = tooltip;
        layer.InvalidateVisual();
    }
}
