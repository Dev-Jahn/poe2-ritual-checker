using System.Runtime.InteropServices;

namespace Ritual.Core;

public record DisplayColorInfo(bool? HdrEnabled, float SdrWhiteScale, string DeviceName);

public static class DisplayColor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Source
    {
        public Luid Adapter;
        public uint Id,
            Mode,
            Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Target
    {
        public Luid Adapter;
        public uint Id,
            Mode,
            Output,
            Rotation,
            Scaling,
            Numerator,
            Denominator,
            Scanline;
        public int Available;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public Source Source;
        public Target Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public uint Type,
            Size;
        public Luid Adapter;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public Header Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct White
    {
        public Header Header;
        public uint Level;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Color
    {
        public Header Header;
        public uint Flags,
            Encoding,
            Bits;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Monitor
    {
        public uint Size;
        public int Left,
            Top,
            Right,
            Bottom,
            WorkLeft,
            WorkTop,
            WorkRight,
            WorkBottom;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref Monitor info);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint paths,
        out uint modes
    );

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] PathInfo[] paths,
        ref uint modeCount,
        nint modes,
        nint topology
    );

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSource(ref SourceName source);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetWhite(ref White white);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetColor(ref Color color);

    public static DisplayColorInfo Read(nint window)
    {
        var monitor = new Monitor { Size = (uint)Marshal.SizeOf<Monitor>(), Name = "" };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor))
            return new(null, 1, "");
        if (GetDisplayConfigBufferSizes(2, out var pathCount, out var modeCount) != 0)
            return new(null, 1, monitor.Name);
        var paths = new PathInfo[pathCount];
        var modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
        try
        {
            if (QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0) != 0)
                return new(null, 1, monitor.Name);
            foreach (var path in paths.Take((int)pathCount))
            {
                var source = new SourceName
                {
                    Header = new()
                    {
                        Type = 1,
                        Size = (uint)Marshal.SizeOf<SourceName>(),
                        Adapter = path.Source.Adapter,
                        Id = path.Source.Id,
                    },
                    Name = "",
                };
                if (GetSource(ref source) != 0 || source.Name != monitor.Name)
                    continue;
                var color = new Color
                {
                    Header = new()
                    {
                        Type = 9,
                        Size = (uint)Marshal.SizeOf<Color>(),
                        Adapter = path.Target.Adapter,
                        Id = path.Target.Id,
                    },
                };
                bool? enabled = GetColor(ref color) == 0 ? (color.Flags & 2) != 0 : null;
                var white = new White
                {
                    Header = new()
                    {
                        Type = 11,
                        Size = (uint)Marshal.SizeOf<White>(),
                        Adapter = path.Target.Adapter,
                        Id = path.Target.Id,
                    },
                };
                float scale =
                    GetWhite(ref white) == 0 && white.Level >= 100 ? white.Level / 1000f : 1;
                return new(enabled, enabled == true ? scale : 1, monitor.Name);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(modes);
        }
        return new(null, 1, monitor.Name);
    }
}
