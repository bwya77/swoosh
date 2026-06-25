using System.Diagnostics;
using System.Runtime.InteropServices;
using Swoosh.Native;

namespace Swoosh.Input;

/// <summary>
/// Captures a mouse-only middle-button drag so users without a touchpad can aim the same HUD.
/// </summary>
public sealed class MouseGestureListener : IDisposable
{
    private readonly Win32.LowLevelMouseProc _proc;
    private IntPtr _hook;
    private bool _tracking;
    private long _trackingStartedMs;
    private const long ButtonStateGraceMs = 150;

    public bool Enabled { get; set; }

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

        if (_tracking && msg != Win32.WM_MBUTTONUP &&
            Environment.TickCount64 - _trackingStartedMs > ButtonStateGraceMs &&
            !Win32.IsKeyDown(Win32.VK_MBUTTON))
        {
            _tracking = false;
            MiddleUp?.Invoke(data.pt);
        }

        if (msg == Win32.WM_MBUTTONDOWN)
        {
            if (!Enabled || (data.flags & Win32.LLMHF_INJECTED) != 0)
                return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

            if (MiddleDown?.Invoke(data.pt) == true)
            {
                _tracking = true;
                _trackingStartedMs = Environment.TickCount64;
            }
        }
        else if (_tracking && msg == Win32.WM_MOUSEMOVE)
        {
            Moved?.Invoke(data.pt);
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        else if (_tracking && msg == Win32.WM_MBUTTONUP)
        {
            _tracking = false;
            MiddleUp?.Invoke(data.pt);
        }

        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

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
