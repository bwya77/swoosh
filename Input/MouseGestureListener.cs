using System.Diagnostics;
using System.Runtime.InteropServices;
using Swoosh.Native;

namespace Swoosh.Input;

/// <summary>
/// Starts the titlebar HUD with the selected trigger: middle button or modifier + left button.
/// </summary>
public sealed class MouseGestureListener : IDisposable
{
    private readonly Win32.LowLevelMouseProc _proc;
    private IntPtr _hook;
    private bool _tracking;
    private bool _releasePending;
    private bool _suppressClick;
    private int _activeUpMessage;
    private int _activeButtonVk;
    private readonly Func<int, bool> _isKeyDown;
    private long _trackingStartedMs;
    private const long ButtonStateGraceMs = 150;

    public bool Enabled { get; set; }
    public int ModifierVk { get; set; }
    public int ActiveModifierVk { get; private set; }

    public event Func<Win32.POINT, bool>? Pressed;
    public event Action<Win32.POINT>? Moved;
    public event Action<Win32.POINT>? Released;

    public MouseGestureListener() : this(Win32.IsKeyDown, installHook: true)
    {
    }

    internal MouseGestureListener(Func<int, bool> isKeyDown, bool installHook = false)
    {
        _isKeyDown = isKeyDown;
        _proc = HookProc;
        if (!installHook) return;
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

        var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
        return ProcessMessage(wParam.ToInt32(), data)
            ? new IntPtr(1)
            : Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    internal bool ProcessMessage(int msg, Win32.MSLLHOOKSTRUCT data)
    {
        if ((data.flags & Win32.LLMHF_INJECTED) != 0) return false;

        if (_releasePending && msg == _activeUpMessage)
        {
            bool suppress = _suppressClick;
            bool complete = _tracking;
            _tracking = false;
            _releasePending = false;
            if (complete) Released?.Invoke(data.pt);
            return suppress;
        }

        RecoverMissedRelease(data.pt);

        if (_tracking && msg == Win32.WM_MOUSEMOVE)
        {
            Moved?.Invoke(data.pt);
            return false;
        }

        if (_releasePending || !Enabled) return false;

        bool modifierTrigger = msg == Win32.WM_LBUTTONDOWN &&
            ModifierVk != 0 && _isKeyDown(ModifierVk);
        bool middleTrigger = ModifierVk == 0 && msg == Win32.WM_MBUTTONDOWN;
        if (!middleTrigger && !modifierTrigger) return false;

        ActiveModifierVk = modifierTrigger ? ModifierVk : 0;
        if (Pressed?.Invoke(data.pt) == true)
        {
            _tracking = true;
            _releasePending = true;
            _activeUpMessage = modifierTrigger ? Win32.WM_LBUTTONUP : Win32.WM_MBUTTONUP;
            _activeButtonVk = modifierTrigger ? Win32.VK_LBUTTON : Win32.VK_MBUTTON;
            _trackingStartedMs = Environment.TickCount64;
            // Leave middle-button behavior untouched; consume the alternative left
            // click so Windows does not start dragging the titlebar.
            _suppressClick = modifierTrigger;
            return _suppressClick;
        }

        ActiveModifierVk = 0;
        return false;
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
        ActiveModifierVk = 0;
    }

    public void RecoverMissedRelease(Win32.POINT pt)
    {
        // A swallowed button-down need not update GetAsyncKeyState. Only the
        // non-capturing middle-button trigger can safely use that recovery check.
        if (!_releasePending || _suppressClick ||
            Environment.TickCount64 - _trackingStartedMs <= ButtonStateGraceMs ||
            _isKeyDown(_activeButtonVk))
            return;

        bool complete = _tracking;
        _tracking = false;
        _releasePending = false;
        if (complete) Released?.Invoke(pt);
    }
}
