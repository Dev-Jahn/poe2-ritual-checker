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
    private readonly Action trade;
    private readonly int[] heldAction = new int[4];
    private readonly DispatcherTimer timer;
    private readonly DateTimeOffset?[] pressed = new DateTimeOffset?[4];
    private readonly bool[] fired = new bool[4];

    public InputService(nint window, Settings settings, Action trigger, Action trade)
    {
        this.settings = settings;
        this.trigger = trigger;
        this.trade = trade;
        source = HwndSource.FromHwnd(window);
        source.AddHook(Hook);
        try
        {
            Configure();
        }
        catch
        {
            source.RemoveHook(Hook);
            throw;
        }
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += Tick;
        timer.Start();
    }

    public void Configure()
    {
        UnregisterHotKey(source.Handle, 81);
        UnregisterHotKey(source.Handle, 82);
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
        if (!RegisterHotKey(source.Handle, 82, 0x4000, (uint)settings.TradeVirtualKey))
        {
            UnregisterHotKey(source.Handle, 81);
            throw new InvalidOperationException(
                "거래소 조회 키가 분석 키 또는 다른 프로그램과 겹칩니다. 다른 키를 선택하세요."
            );
        }
        Array.Clear(pressed);
        Array.Clear(fired);
        Array.Clear(heldAction);
    }

    private nint Hook(nint h, int message, nint w, nint l, ref bool handled)
    {
        if (message == 0x0312 && (w == 81 || w == 82))
        {
            handled = true;
            if (GameWindow.IsGame(GameWindow.GetForegroundWindow()))
                if (w == 82)
                    trade();
                else
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
            Array.Clear(heldAction);
            return;
        }
        for (uint i = 0; i < 4; i++)
        {
            if (settings.ControllerIndex >= 0 && settings.ControllerIndex != i)
                continue;
            if (GetState(i, out var state) != 0)
            {
                pressed[i] = null;
                fired[i] = false;
                heldAction[i] = 0;
                continue;
            }
            int action =
                settings.TradeControllerButtons != 0
                && (state.Gamepad.Buttons & settings.TradeControllerButtons)
                    == settings.TradeControllerButtons
                    ? 82
                : (state.Gamepad.Buttons & settings.ControllerButtons) == settings.ControllerButtons
                    ? 81
                : 0;
            // Keep a completed chord latched until released; the trade chord includes analysis buttons.
            if (fired[i] && action != 0)
                continue;
            if (action != heldAction[i])
            {
                pressed[i] = null;
                fired[i] = false;
                heldAction[i] = action;
            }
            if (action == 0)
                continue;
            pressed[i] ??= DateTimeOffset.UtcNow;
            if (
                !fired[i]
                && DateTimeOffset.UtcNow - pressed[i]
                    >= TimeSpan.FromMilliseconds(settings.ControllerHoldMs)
            )
            {
                fired[i] = true;
                if (action == 82)
                    trade();
                else
                    trigger();
            }
        }
    }

    public void Dispose()
    {
        timer.Stop();
        UnregisterHotKey(source.Handle, 81);
        UnregisterHotKey(source.Handle, 82);
        source.RemoveHook(Hook);
    }
}
