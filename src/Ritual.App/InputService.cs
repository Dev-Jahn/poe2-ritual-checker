using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Ritual.Core;

namespace Ritual.App;

public sealed class InputService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState(uint index, out State state);

    [StructLayout(LayoutKind.Sequential)]
    private struct Pad
    {
        public ushort Buttons;
        public byte LT,
            RT;
        public short LX,
            LY,
            RX,
            RY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Packet;
        public Pad Gamepad;
    }

    private readonly HwndSource source;
    private readonly Settings settings;
    private readonly Action trigger;
    private readonly DispatcherTimer timer;
    private readonly DateTimeOffset?[] pressed = new DateTimeOffset?[4];
    private readonly bool[] fired = new bool[4];

    public InputService(nint window, Settings settings, Action trigger)
    {
        this.settings = settings;
        this.trigger = trigger;
        source = HwndSource.FromHwnd(window);
        source.AddHook(Hook);
        Configure();
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += Tick;
        timer.Start();
    }

    public void Configure()
    {
        UnregisterHotKey(source.Handle, 81);
        if (
            !RegisterHotKey(
                source.Handle,
                81,
                settings.KeyboardModifiers | 0x4000,
                (uint)settings.KeyboardVirtualKey
            )
        )
            throw new InvalidOperationException(
                "분석 단축키가 다른 프로그램에서 사용 중입니다. 다른 키로 설정하세요."
            );
        Array.Clear(pressed);
        Array.Clear(fired);
    }

    private nint Hook(nint h, int message, nint w, nint l, ref bool handled)
    {
        if (message == 0x0312 && w == 81)
        {
            handled = true;
            if (GameWindow.IsGame(GameWindow.GetForegroundWindow()))
                trigger();
        }
        return 0;
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (
            !settings.ControllerEnabled
            || settings.ControllerButtons == 0
            || !GameWindow.IsGame(GameWindow.GetForegroundWindow())
        )
        {
            Array.Clear(pressed);
            Array.Clear(fired);
            return;
        }
        for (uint i = 0; i < 4; i++)
        {
            if (settings.ControllerIndex >= 0 && settings.ControllerIndex != i)
                continue;
            if (
                GetState(i, out var state) != 0
                || (state.Gamepad.Buttons & settings.ControllerButtons)
                    != settings.ControllerButtons
            )
            {
                pressed[i] = null;
                fired[i] = false;
                continue;
            }
            pressed[i] ??= DateTimeOffset.UtcNow;
            if (
                !fired[i]
                && DateTimeOffset.UtcNow - pressed[i]
                    >= TimeSpan.FromMilliseconds(settings.ControllerHoldMs)
            )
            {
                fired[i] = true;
                trigger();
            }
        }
    }

    public void Dispose()
    {
        timer.Stop();
        UnregisterHotKey(source.Handle, 81);
        source.RemoveHook(Hook);
    }
}
