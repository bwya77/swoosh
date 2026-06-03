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
        _chip.ShowDesktops(null, false);
        Log.Write("HoldEngaged");
    }

    private void OnHoldUpdated(DesktopDirection? dir, double progress)
    {
        if (!_armed) return;
        _chip.ShowDesktops(dir, false);
    }

    private void OnDesktopMove(DesktopDirection dir)
    {
        _preview.Hide();
        if (!_armed) { return; }
        bool ok = VirtualDesktop.MoveAdjacent(_target, dir, out string diag);
        Log.Write($"DesktopMove dir={dir} ok={ok} {diag}");
        if (ok)
        {
            Win32.SetForegroundWindow(_target);
            _chip.ShowDesktops(dir, true); // fill target blue, then auto-hide
        }
        else
        {
            _chip.Hide();
        }
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
