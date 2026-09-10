using System.Runtime.InteropServices;
using OpenCvSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Ritual.Core;

public sealed class WgcCapture : IDisposable
{
    [
        ComImport,
        Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [
        ComImport,
        Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(in Guid iid);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out nint str);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint str);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint window,
        uint attribute,
        out NativeRect rect,
        int size
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left,
            Top,
            Right,
            Bottom;
    }

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDirect3DDevice winDevice;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;
    private readonly nint window;
    private readonly byte[] toneLut = new byte[65536];
    public DisplayColorInfo DisplayColorInfo { get; }
    private readonly object sync = new();
    private TaskCompletionSource<CapturedFrame>? pending;
    private bool disposed;

    public WgcCapture(nint window)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException("Windows Graphics Capture 미지원");
        this.window = window;
        DisplayColorInfo = DisplayColor.Read(window);
        for (int i = 0; i < toneLut.Length; i++)
            toneLut[i] = HdrToneMap.Channel(
                (float)BitConverter.UInt16BitsToHalf((ushort)i),
                DisplayColorInfo.SdrWhiteScale
            );
        D3D11
            .D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out device,
                out context
            )
            .CheckError();
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(
            CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr)
        );
        try
        {
            winDevice = MarshalInterface<IDirect3DDevice>.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
        var item = CreateItem(window);
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winDevice,
            DirectXPixelFormat.R16G16B16A16Float,
            2,
            item.Size
        );
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        pool.FrameArrived += FrameArrived;
        session.StartCapture();
    }

    private static GraphicsCaptureItem CreateItem(nint window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var str));
        nint factory = 0;
        try
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(str, in iid, out factory));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var item = interop.CreateForWindow(window, in itemId);
            try
            {
                return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
                Marshal.ReleaseComObject(interop);
            }
        }
        finally
        {
            if (factory != 0)
                Marshal.Release(factory);
            WindowsDeleteString(str);
        }
    }

    public async Task<CapturedFrame> CaptureAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<CapturedFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending is not null)
                throw new InvalidOperationException("Capture already pending");
            pending = tcs;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var registration = deadline.Token.Register(() =>
        {
            lock (sync)
            {
                if (pending == tcs)
                    pending = null;
            }
            tcs.TrySetCanceled(deadline.Token);
        });
        return await tcs.Task;
    }

    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (sync)
        {
            if (disposed)
                return;
            using var frame = sender.TryGetNextFrame();
            if (frame is null || pending is null)
                return;
            var tcs = pending;
            pending = null;
            try
            {
                var result = Read(frame);
                if (!tcs.TrySetResult(result))
                    result.Dispose();
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        }
    }

    private unsafe CapturedFrame Read(Direct3D11CaptureFrame frame)
    {
        var abi = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        ID3D11Texture2D texture;
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(abi);
            try
            {
                var iid = typeof(ID3D11Texture2D).GUID;
                texture = new ID3D11Texture2D(access.GetInterface(in iid));
            }
            finally
            {
                Marshal.ReleaseComObject(access);
            }
        }
        finally
        {
            MarshalInterface<IDirect3DSurface>.DisposeAbi(abi);
        }
        using (texture)
        {
            var desc = texture.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            desc.MiscFlags = ResourceOptionFlags.None;
            using var staging = device.CreateTexture2D(desc);
            context.CopyResource(staging, texture);
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            Mat? output = null;
            try
            {
                int width = Math.Min((int)desc.Width, frame.ContentSize.Width),
                    height = Math.Min((int)desc.Height, frame.ContentSize.Height);
                output = new Mat(height, width, MatType.CV_8UC3);
                Parallel.For(
                    0,
                    height,
                    new ParallelOptions { MaxDegreeOfParallelism = 4 },
                    y =>
                    {
                        var source = (ushort*)((byte*)mapped.DataPointer + y * mapped.RowPitch);
                        var target = (byte*)output.Ptr(y);
                        for (int x = 0; x < width; x++)
                        {
                            target[x * 3] = toneLut[source[x * 4 + 2]];
                            target[x * 3 + 1] = toneLut[source[x * 4 + 1]];
                            target[x * 3 + 2] = toneLut[source[x * 4]];
                        }
                    }
                );
                var bounds = GameWindow.Bounds(window);
                if (DwmGetWindowAttribute(window, 9, out var outer, 16) == 0)
                {
                    var crop = new Box(
                        bounds.X - outer.Left,
                        bounds.Y - outer.Top,
                        bounds.Width,
                        bounds.Height
                    );
                    if (VisionEngine.Within(crop, output))
                    {
                        using var roi = new Mat(output, crop.Rect());
                        var client = roi.Clone();
                        output.Dispose();
                        output = client;
                    }
                    else if (output.Width != bounds.Width || output.Height != bounds.Height)
                        throw new InvalidDataException("캡처와 게임 클라이언트 좌표 불일치");
                }
                return new(
                    output,
                    bounds,
                    window,
                    "Windows Graphics Capture FP16",
                    $"{(DisplayColorInfo.HdrEnabled == true ? "HDR" : DisplayColorInfo.HdrEnabled == false ? "SDR" : "색 공간 미확인")} → SDR · 흰색 기준 {DisplayColorInfo.SdrWhiteScale * 80:0}nit · 게임 HDR 검증 대기"
                );
            }
            catch
            {
                output?.Dispose();
                throw;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            pending?.TrySetCanceled();
            pending = null;
            pool.FrameArrived -= FrameArrived;
            session.Dispose();
            pool.Dispose();
            winDevice.Dispose();
            context.Dispose();
            device.Dispose();
        }
    }
}
