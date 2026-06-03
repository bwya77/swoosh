using Swoosh.Gestures;
using Swoosh.Hotkeys;
using Swoosh.Input;
using Swoosh.Native;
using Swoosh.Snapping;
using Swoosh.UI;

namespace Swoosh;

/// <summary>Wires raw input + gestures + hotkeys to window snapping.</summary>
public sealed class SwooshController : IDisposable
{
    private readonly MessageWindow _window;
    private readonly RawTouchpadListener _touchpad;
    private readonly GestureEngine _gestures = new();
    private readonly WindowSnapper _snapper = new();
    private readonly HotkeyListener _hotkeys;
    private readonly PreviewOverlay _preview = new();
    private readonly CursorChipOverlay _chip = new();
    private readonly DebugOverlay _debug = new();

    private IntPtr _target;
    private bool _armed;
    private int _deskCount = 2;
    private int _deskIndex;

    // 5-finger free-move state: the window's live top-left (in physical pixels)
    // plus the monitor work-area size used to map pad motion 1:1 onto the screen.
    private bool _free;
    private double _freeWinX, _freeWinY;
    private int _freeWorkW = 1, _freeWorkH = 1;

    /// <summary>How much screen the window covers per unit of pad travel (1.0 = pad spans the monitor).</summary>
    private const double FreeMoveScale = 1.0;

    public bool GesturesEnabled { get; set; } = true;

    public SwooshController()
    {
        _window = new MessageWindow("SwooshMsgWindow");
        _touchpad = new RawTouchpadListener(_window);
        _hotkeys = new HotkeyListener(_window);
        Log.Write($"Controller up. msgHwnd=0x{_window.Handle.ToInt64():X} hotkeys={_hotkeys.RegisteredCount}");

        _touchpad.FrameDecoded += OnFrame;
        _gestures.GestureBegan += OnGestureBegan;
        _gestures.GestureUpdated += OnGestureUpdated;
        _gestures.GestureCompleted += OnGestureCompleted;
        _gestures.GestureCancelled += OnGestureCancelled;
        _gestures.HoldEngaged += OnHoldEngaged;
        _gestures.HoldUpdated += OnHoldUpdated;
        _gestures.DesktopMove += OnDesktopMove;
        _gestures.FreeMoveBegan += OnFreeMoveBegan;
        _gestures.FreeMoveDelta += OnFreeMoveDelta;
        _gestures.FreeMoveEnded += OnFreeMoveEnded;
        _hotkeys.Triggered += OnHotkey;
    }

    private void OnFrame(TouchFrame frame)
    {
        _debug.Render(frame);
        if (GesturesEnabled)
            _gestures.Process(frame);
    }

    private void OnGestureBegan(int fingers)
    {
        // Arm only when the cursor is over a manageable window's titlebar.
        _target = _snapper.ArmTarget(out string diag);
        _armed = _target != IntPtr.Zero;
        if (_armed) _chip.ShowSnap(SnapZone.None, 0);
        Log.Write($"GestureBegan fingers={fingers} armed={_armed} {diag}");
    }

    private void OnGestureUpdated(SwipeDirection dir, double progress)
    {
        if (!_armed) { _preview.Hide(); _chip.Hide(); return; }
        if (dir == SwipeDirection.None || progress <= 0)
        {
            _preview.Hide();
            _chip.ShowSnap(SnapZone.None, 0);
            return;
        }
        var zone = SnapZoneMap.FromDirection(dir);
        var work = _snapper.WorkAreaFor(_target);
        Win32.RECT rect = zone == SnapZone.Minimize
            ? MinimizeHint(work)
            : WindowSnapper.ZoneRect(work, zone);
        _preview.ShowZone(rect, progress);
        _chip.ShowSnap(zone, progress);
    }

    private void OnGestureCompleted(SwipeDirection dir)
    {
        _preview.Hide();
        _chip.Hide();
        var zone = SnapZoneMap.FromDirection(dir);
        Log.Write($"GestureCompleted dir={dir} zone={zone} armed={_armed}");
        if (!_armed) return;
        _snapper.Apply(_target, zone);
        if (zone != SnapZone.Minimize)
            Win32.SetForegroundWindow(_target);
        _armed = false;
    }

    private void OnGestureCancelled()
    {
        _preview.Hide();
        _chip.Hide();
        _armed = false;
    }

    private void OnHoldEngaged()
    {
        if (!_armed) return;
        // Audible "click" stand-in for touchpad haptics (not exposed on Windows).
        Win32.MessageBeep(0xFFFFFFFF);
        if (VirtualDesktop.GetLayout(out int cnt, out int idx, out string ld))
        {
            _deskCount = Math.Max(1, cnt);
            _deskIndex = Math.Clamp(idx, 0, _deskCount - 1);
        }
        _chip.ShowDesktopStrip(_deskCount, _deskIndex, null);
        Log.Write($"HoldEngaged layout({ld})");
    }

    private void OnHoldUpdated(DesktopDirection? lean, double progress)
    {
        if (!_armed) return;
        _chip.ShowDesktopStrip(_deskCount, _deskIndex, lean);
    }

    private void OnDesktopMove(DesktopDirection dir)
    {
        _preview.Hide();
        if (!_armed) { return; }
        // Carry the HUD overlay to the new desktop so it stays visible, and keep
        // the gesture armed so the user can step to further desktops (or back)
        // without lifting their fingers. The hold ends only on release.
        bool ok = VirtualDesktop.MoveAdjacent(_target, dir, _chip.Handle, out string diag);
        Log.Write($"DesktopMove dir={dir} ok={ok} {diag}");
        if (ok)
        {
            // We follow the window, so the current desktop is now the neighbor.
            _deskIndex = Math.Clamp(_deskIndex + (dir == DesktopDirection.Right ? 1 : -1), 0, _deskCount - 1);
            Win32.SetForegroundWindow(_target);
            _chip.ShowDesktopStrip(_deskCount, _deskIndex, null);
        }
    }

    private void OnFreeMoveBegan()
    {
        // Free-move arms exactly like a snap: only act over a manageable titlebar.
        _target = _snapper.ArmTarget(out string diag);
        _armed = _target != IntPtr.Zero;
        if (!_armed) { _free = false; Log.Write($"FreeMoveBegan not-armed {diag}"); return; }

        // A maximized/minimized window can't be nudged; restore it first so the
        // window has a real floating rect to move from.
        long style = Win32.GetWindowLong(_target, Win32.GWL_STYLE);
        if ((style & (Win32.WS_MAXIMIZE | Win32.WS_MINIMIZE)) != 0)
            Win32.ShowWindow(_target, Win32.SW_RESTORE);

        _free = true;
        if (Win32.GetWindowRect(_target, out var wr)) { _freeWinX = wr.Left; _freeWinY = wr.Top; }
        var work = _snapper.WorkAreaFor(_target);
        _freeWorkW = Math.Max(1, work.Width);
        _freeWorkH = Math.Max(1, work.Height);

        // The window itself is the live feedback here — no snap/desktop HUD.
        _preview.Hide();
        _chip.Hide();
        Win32.MessageBeep(0xFFFFFFFF);
        Log.Write($"FreeMoveBegan armed pos=({_freeWinX:F0},{_freeWinY:F0}) work={_freeWorkW}x{_freeWorkH}");
    }

    private void OnFreeMoveDelta(double ddx, double ddy)
    {
        if (!_free || !_armed) return;
        // Map normalized pad travel onto the monitor: a full pad sweep moves the
        // window a full work-area span (touchpad acts as the monitor).
        _freeWinX += ddx * _freeWorkW * FreeMoveScale;
        _freeWinY += ddy * _freeWorkH * FreeMoveScale;
        Win32.SetWindowPos(_target, IntPtr.Zero,
            (int)Math.Round(_freeWinX), (int)Math.Round(_freeWinY), 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER | Win32.SWP_NOACTIVATE);
    }

    private void OnFreeMoveEnded(bool wasTap)
    {
        if (_free && _armed)
        {
            if (wasTap)
            {
                // A brief, near-still five-finger touch is the Swish-style
                // "center the window" gesture. Five-finger taps have no native
                // Windows gesture, so there's no OS conflict to fight. Recenter
                // the same window we armed (it barely moved during the tap).
                _preview.Hide();
                _chip.Hide();
                _snapper.CenterOnMonitor(_target);
            }
            Win32.SetForegroundWindow(_target);
        }
        _free = false;
        _armed = false;
    }

    private void OnHotkey(SnapZone zone)
    {
        IntPtr h = _snapper.WindowUnderCursor();
        bool man = _snapper.IsManageable(h);
        Log.Write($"OnHotkey zone={zone} hwnd=0x{h.ToInt64():X} manageable={man} title='{Win32.GetWindowTitle(h)}'");
        if (!man) return;
        _snapper.Apply(h, zone);
        if (zone != SnapZone.Minimize)
            Win32.SetForegroundWindow(h);
    }

    private static Win32.RECT MinimizeHint(Win32.RECT work)
    {
        int w = work.Width / 4, h = 48;
        int x = work.Left + (work.Width - w) / 2;
        int y = work.Bottom - h - 8;
        return new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    public void ToggleDebugOverlay() => _debug.Toggle();

    public void Dispose()
    {
        _touchpad.Dispose();
        _hotkeys.Dispose();
        _preview.Close();
        _chip.Close();
        _debug.Close();
        _window.Dispose();
    }
}
