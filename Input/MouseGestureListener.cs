using System.Diagnostics;
using System.Runtime.InteropServices;
using Swoosh.Native;
using Swoosh.Settings;

namespace Swoosh.Input;

/// <summary>
/// Captures a mouse-only middle-button drag so users without a touchpad can aim the same HUD.
/// </summary>
public sealed class MouseGestureListener : IDisposable
{
    private readonly Win32.LowLevelMouseProc _proc;
    private IntPtr _hook;
    private bool _tracking;
    private MouseHudTriggerButton _trackingButton;

    public bool Enabled { get; set; }
    public MouseHudTriggerButton Button { get; set; } = MouseHudTriggerButton.Middle;

    public event Func<Win32.POINT, bool>? MiddleDown;
    public event Action<Win32.POINT>? Moved;
    public event Action<Win32.POINT>? MiddleUp;

    public MouseGestureListener()
    {
        _proc = HookProc;
        using var cur = Process.GetCurrentProcess();
        using var mod = cur.MainModule;
        IntPtr hMod = Win32.GetModuleHandle(mod?.ModuleName);
        _hook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero)
            Swoosh.Log.Write($"MouseGestureListener hook failed err={Marshal.GetLastWin32Error()}");
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _hook == IntPtr.Zero)
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);

        if (msg == DownMessage(Button))
        {
            if (!Enabled || (data.flags & Win32.LLMHF_INJECTED) != 0)
                return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

            if (MiddleDown?.Invoke(data.pt) == true)
            {
                _tracking = true;
                _trackingButton = Button;
                return new IntPtr(1);
            }
        }
        else if (_tracking && msg == Win32.WM_MOUSEMOVE)
        {
            Moved?.Invoke(data.pt);
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        else if (_tracking && msg == UpMessage(_trackingButton))
        {
            _tracking = false;
            MiddleUp?.Invoke(data.pt);
            return new IntPtr(1);
        }

        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static int DownMessage(MouseHudTriggerButton button) => button switch
    {
        MouseHudTriggerButton.Right => Win32.WM_RBUTTONDOWN,
        _ => Win32.WM_MBUTTONDOWN,
    };

    private static int UpMessage(MouseHudTriggerButton button) => button switch
    {
        MouseHudTriggerButton.Right => Win32.WM_RBUTTONUP,
        _ => Win32.WM_MBUTTONUP,
    };

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void CancelTracking()
    {
        _tracking = false;
    }
}
