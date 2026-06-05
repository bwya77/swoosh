using Swoosh.Gestures;
using Swoosh.Hotkeys;
using Swoosh.Input;
using Swoosh.Native;
using Swoosh.Settings;
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
    private readonly SwooshStats _stats = new();

    private IntPtr _target;
    private bool _armed;
    private int _deskCount = 2;
    private int _deskIndex;

    // Pinch-out fullscreen remembers the window's pre-fullscreen rect so a
    // following pinch-in restores it to exactly where it was.
    private Win32.RECT _preMaxRect;
    private IntPtr _preMaxHwnd;
    private bool _hasPreMax;

    // 5-finger free-move state: the window's live top-left (in physical pixels)
    // plus the monitor work-area size used to map pad motion 1:1 onto the screen.
    private bool _free;
    private double _freeWinX, _freeWinY;
    private double _freeWinW, _freeWinH;
    private int _freeWorkW = 1, _freeWorkH = 1;

    /// <summary>How much screen the window covers per unit of pad travel (1.0 = pad spans the monitor).</summary>
    private const double FreeMoveScale = 1.0;

    /// <summary>Amplifies the raw hand-spread ratio so a modest expand/contract makes a
    /// noticeable size change.</summary>
    private const double FreeResizeGain = 1.6;

    /// <summary>Smallest window the five-finger resize will shrink to (physical pixels).</summary>
    private const int MinFreeW = 260, MinFreeH = 180;

    public bool GesturesEnabled { get; set; } = true;

    // Live preview: when on, the real window moves to the target zone as you swipe
    // (instead of the translucent overlay). The original rect is captured at gesture
    // start so Esc can restore it.
    private bool _livePreview;
    private Win32.RECT _liveOrigRect;
    private bool _liveWasMax;
    private bool _liveMoved;
    private SnapZone _liveZone = SnapZone.None;

    // Per-gesture enable flags (Swish-style: each snap gesture can be turned off
    // individually from the Snapping settings). Default on.
    private bool _maximizeEnabled = true;
    private bool _halvesEnabled = true;
    private bool _quartersEnabled = true;
    private bool _minimizeEnabled = true;
    private bool _centerEnabled = true;

    // Thirds: when enabled and the chosen modifier is held during a snap swipe,
    // the target becomes a full-height column / full-width row third instead of
    // the default halves and quarters.
    private bool _gridModifierEnabled = true;
    private int _gridModifierVk = Win32.VK_SHIFT;

    // Move-to-display: when enabled and the chosen modifier is held during a
    // two-finger swipe, the window is sent to the adjacent physical monitor
    // (with a live monitor-map HUD) instead of snapping. Default modifier is Alt.
    private bool _monitorModifierEnabled = true;
    private int _monitorModifierVk = Win32.VK_MENU;

    // Monitor topology cached for the duration of a move-to-display gesture.
    private List<MonitorLayout.Mon>? _monAll;
    private MonitorLayout.Mon? _monCur;

    // Minimum cross-axis / main-axis ratio for a swipe to count as a diagonal (corner
    // cell) rather than a straight column/row. Driven by the sensitivity slider: higher
    // sensitivity -> lower ratio -> diagonals trigger more readily.
    private double _thirdsDiagRatio = 0.65;

    /// <summary>Apply persisted settings to the live engine (no restart needed).</summary>
    public void ApplySettings(AppSettings s)
    {
        GesturesEnabled = s.GesturesEnabled;
        _maximizeEnabled = s.MaximizeEnabled;
        _halvesEnabled = s.HalvesEnabled;
        _quartersEnabled = s.QuartersEnabled;
        _minimizeEnabled = s.MinimizeEnabled;
        _centerEnabled = s.CenterEnabled;
        _snapper.AnimateSnaps = s.AnimateSnaps;
        _snapper.GridSpacing = Math.Clamp(s.GridSpacing, 0, 10);
        _gestures.IdleCancelMs = s.CancelTimeoutSeconds > 0
            ? (long)Math.Round(Math.Clamp(s.CancelTimeoutSeconds, 0, 10) * 1000)
            : 0;
        _gridModifierEnabled = s.GridModifierEnabled;
        _gridModifierVk = s.GridModifier switch
        {
            GridModifier.Ctrl => Win32.VK_CONTROL,
            GridModifier.Alt => Win32.VK_MENU,
            _ => Win32.VK_SHIFT,
        };
        _thirdsDiagRatio = 0.9 - 0.5 * Math.Clamp(s.Sensitivity, 0, 1);
        _monitorModifierEnabled = s.MonitorMoveEnabled;
        _monitorModifierVk = s.MonitorMoveModifier switch
        {
            GridModifier.Ctrl => Win32.VK_CONTROL,
            GridModifier.Shift => Win32.VK_SHIFT,
            _ => Win32.VK_MENU,
        };
        _chip.ApplyAppearance(s.AnimateSnaps, s.OverlayUseAccent, s.OverlayColor);
        _preview.ApplyAppearance(s.AnimateSnaps, s.OverlayUseAccent, s.OverlayColor);
        _debug.SetVisible(s.DebugOverlay);
        _livePreview = s.LivePreview;
    }

    /// <summary>Resolve a swipe to a zone, honoring the thirds modifier if held.</summary>
    private SnapZone MapZone(SwipeDirection dir) =>
        ThirdsActive ? ThirdsZone(_lastVecX, _lastVecY) : Gate(SnapZoneMap.FromDirection(dir));

    /// <summary>Nullify a base snap zone whose gesture has been disabled in settings, so
    /// the preview shows nothing and the commit is a no-op. Thirds cells are unaffected.</summary>
    private SnapZone Gate(SnapZone z) => z switch
    {
        SnapZone.Maximize => _maximizeEnabled ? z : SnapZone.None,
        SnapZone.Minimize => _minimizeEnabled ? z : SnapZone.None,
        SnapZone.LeftHalf or SnapZone.RightHalf => _halvesEnabled ? z : SnapZone.None,
        SnapZone.TopLeft or SnapZone.TopRight or SnapZone.BottomLeft or SnapZone.BottomRight
            => _quartersEnabled ? z : SnapZone.None,
        _ => z,
    };

    private bool ThirdsActive => _gridModifierEnabled && Win32.IsKeyDown(_gridModifierVk);

    /// <summary>True while the move-to-display modifier is held.</summary>
    private bool MonitorMoveActive => _monitorModifierEnabled && Win32.IsKeyDown(_monitorModifierVk);

    // Latest raw swipe vector (signed, pad-normalized; Y grows downward), tracked so
    // thirds targets can be chosen by magnitude at both preview and commit time.
    private double _lastVecX, _lastVecY;

    // Thirds magnitude bands: below Center -> centered third; below Big -> two-thirds
    // to that side; beyond -> one-third pinned to that edge.
    private const double ThirdsCenterBand = 0.06;
    private const double ThirdsBigBand = 0.13;

    /// <summary>Pick a Swish-style third from the raw swipe vector. A genuine diagonal
    /// lands a 1/3 x 1/3 corner cell; otherwise the dominant axis gives a full-height
    /// column or full-width row, sized one-third / two-thirds / centered by magnitude.</summary>
    private SnapZone ThirdsZone(double dx, double dy)
    {
        double ax = Math.Abs(dx), ay = Math.Abs(dy);
        double mx = Math.Max(ax, ay);
        if (mx < 0.03) return SnapZone.None;

        // Diagonal: both axes meaningful and balanced enough (per the sensitivity
        // slider) -> a corner cell. A lower sensitivity demands a more perfect diagonal,
        // so a sideways swipe with a little vertical drift stays a column.
        double mn = Math.Min(ax, ay);
        if (mn >= ThirdsCenterBand && mn / mx >= _thirdsDiagRatio)
        {
            bool left = dx < 0, top = dy < 0;
            return (left, top) switch
            {
                (true, true) => SnapZone.ThirdTopLeft,
                (false, true) => SnapZone.ThirdTopRight,
                (true, false) => SnapZone.ThirdBottomLeft,
                _ => SnapZone.ThirdBottomRight,
            };
        }

        if (ax >= ay) // horizontal swipe -> full-height column
        {
            if (ax < ThirdsCenterBand) return SnapZone.CenterThird;
            if (dx < 0) return ax < ThirdsBigBand ? SnapZone.LeftTwoThird : SnapZone.LeftThird;
            return ax < ThirdsBigBand ? SnapZone.RightTwoThird : SnapZone.RightThird;
        }

        // vertical swipe -> full-width row (pad Y grows downward, so dy<0 is up)
        if (ay < ThirdsCenterBand) return SnapZone.CenterRowThird;
        if (dy < 0) return ay < ThirdsBigBand ? SnapZone.TopTwoThird : SnapZone.TopThird;
        return ay < ThirdsBigBand ? SnapZone.BottomTwoThird : SnapZone.BottomThird;
    }

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
        _gestures.SwipeRaw += OnSwipeRaw;
        _gestures.HoldEngaged += OnHoldEngaged;
        _gestures.HoldUpdated += OnHoldUpdated;
        _gestures.DesktopMove += OnDesktopMove;
        _gestures.MonitorMoveUpdated += OnMonitorMoveUpdated;
        _gestures.MonitorMove += OnMonitorMove;
        _gestures.FreeMoveBegan += OnFreeMoveBegan;
        _gestures.FreeMoveDelta += OnFreeMoveDelta;
        _gestures.FreeMoveEnded += OnFreeMoveEnded;
        _gestures.PinchOut += OnPinchOut;
        _gestures.PinchIn += OnPinchIn;
        _gestures.PinchUpdated += OnPinchUpdated;
        _hotkeys.Triggered += OnHotkey;
    }

    private void OnFrame(TouchFrame frame)
    {
        _debug.Render(frame);
        if (!GesturesEnabled) return;

        // Esc aborts an in-progress gesture immediately (the HUD fades out).
        if ((_armed || _free) && Win32.IsKeyDown(Win32.VK_ESCAPE))
        {
            _gestures.Cancel();
            return;
        }

        // Reflect the live modifier state so the engine uses the smaller thirds
        // dead-zone the moment the key goes down (even mid-gesture). Move-to-display
        // takes precedence over thirds when both modifiers are held.
        bool mm = MonitorMoveActive;
        _gestures.MonitorMoveMode = mm;
        _gestures.ThirdsMode = !mm && ThirdsActive;
        _gestures.Process(frame);
    }

    private void OnSwipeRaw(double dx, double dy)
    {
        _lastVecX = dx;
        _lastVecY = dy;
    }

    private void OnGestureBegan(int fingers)
    {
        // Arm only when the cursor is over a manageable window's titlebar.
        _lastVecX = _lastVecY = 0;
        _monAll = null;
        _monCur = null;
        _target = _snapper.ArmTarget(out string diag);
        _armed = _target != IntPtr.Zero;
        _liveMoved = false;
        _liveZone = SnapZone.None;
        if (_armed)
        {
            if (MonitorMoveActive)
            {
                // Move-to-display: show the physical monitor map instead of the snap chip.
                CacheMonitors();
                ShowMonitorMapForTarget(null);
            }
            else
            {
                if (_livePreview)
                {
                    // Live preview: remember where the window started so a retreat or Esc
                    // can put it back. The chip HUD still shows; the overlay rectangle is
                    // replaced by the real window moving.
                    _liveWasMax = WindowSnapper.IsMaximized(_target);
                    Win32.GetWindowRect(_target, out _liveOrigRect);
                }
                _chip.ShowSnap(SnapZone.None, 0);
            }
        }
        Log.Write($"GestureBegan fingers={fingers} armed={_armed} live={_livePreview} {diag}");
    }

    private void CacheMonitors()
    {
        _monAll = MonitorLayout.All();
        _monCur = MonitorLayout.ForWindow(_target, _monAll);
    }

    private void ShowMonitorMapForTarget(MonitorDirection? target)
    {
        if (_monCur is not { } cur || _monAll is null) return;
        var (u, d, l, r) = MonitorLayout.Neighbors(cur, _monAll);
        _chip.ShowMonitorMap(u, d, l, r, target);
    }

    private void OnMonitorMoveUpdated(MonitorDirection? dir, double progress)
    {
        if (!_armed) return;
        if (_monAll is null) CacheMonitors();
        _preview.Hide();
        ShowMonitorMapForTarget(dir);
    }

    private void OnMonitorMove(MonitorDirection dir)
    {
        _preview.Hide();
        _chip.Hide();
        if (_armed && _monCur is { } cur && _monAll is not null)
        {
            var dst = MonitorLayout.Adjacent(cur, dir, _monAll);
            if (dst is { } d)
            {
                _snapper.MoveToMonitor(_target, cur.Work, d.Work);
                Win32.ForceForeground(_target);
                _stats.Add();
                Log.Write($"MonitorMove dir={dir} moved");
            }
            else
            {
                Log.Write($"MonitorMove dir={dir} no-neighbor");
            }
        }
        _armed = false;
        _monAll = null;
        _monCur = null;
    }

    private void OnGestureUpdated(SwipeDirection dir, double progress)
    {
        if (!_armed) { _preview.Hide(); _chip.Hide(); return; }
        if (dir == SwipeDirection.None || progress <= 0)
        {
            if (_livePreview)
            {
                RestoreLiveOriginal();
                _liveZone = SnapZone.None;
                _chip.ShowSnap(SnapZone.None, 0);
                return;
            }
            _preview.Hide();
            _chip.ShowSnap(SnapZone.None, 0);
            return;
        }
        var zone = MapZone(dir);
        if (zone == SnapZone.None)
        {
            // A disabled (gated-off) gesture has no target of its own. Leave the preview
            // exactly as it is rather than hiding it: a diagonal swipe (e.g. down-right
            // with Minimize disabled) spends many frames classified as the pure-axis
            // direction, and hiding on each one killed the overlay's glide so the snap
            // looked like the window just teleported. Doing nothing keeps the glide to the
            // real neighbouring zone smooth, and because we never call ZoneRect(None) the
            // old full-screen "maximize" flash can't happen either.
            return;
        }

        if (_livePreview)
        {
            // Move the real window to the target zone as a live preview. Only retarget when
            // the zone actually changes so we don't restart the glide every frame. Minimize
            // can't be previewed by hiding the window, so it stays put until commit. The chip
            // HUD still tracks the zone alongside the live window.
            if (zone != _liveZone)
            {
                if (zone == SnapZone.Minimize) RestoreLiveOriginal();
                else
                {
                    // Bring the window forward on the first live move so the preview is
                    // actually visible (not hidden behind other windows).
                    if (!_liveMoved) Win32.ForceForeground(_target);
                    _snapper.Apply(_target, zone);
                    _liveMoved = true;
                }
                _liveZone = zone;
            }
            _chip.ShowSnap(zone, progress);
            return;
        }

        var work = _snapper.WorkAreaFor(_target);
        Win32.RECT rect = zone == SnapZone.Minimize
            ? MinimizeHint(work)
            : WindowSnapper.ZoneRect(work, zone);
        _preview.ShowZone(rect, progress);
        _chip.ShowSnap(zone, progress);
    }

    /// <summary>Put the window back where it was when a live-preview gesture started
    /// (used when the swipe retreats below the dead-zone or the gesture is cancelled).</summary>
    private void RestoreLiveOriginal()
    {
        if (!_liveMoved || _target == IntPtr.Zero) return;
        if (_liveWasMax) _snapper.Apply(_target, SnapZone.Maximize);
        else _snapper.RestoreToRect(_target, _liveOrigRect);
        _liveMoved = false;
    }

    private void OnGestureCompleted(SwipeDirection dir)
    {
        _preview.Hide();
        _chip.Hide();
        var zone = MapZone(dir);
        if (!_armed) return;
        if (zone == SnapZone.None)
        {
            // Nothing aimed: in live preview, put the window back where it started.
            if (_livePreview) RestoreLiveOriginal();
            _armed = false;
            _liveMoved = false;
            return;
        }
        _snapper.Apply(_target, zone);
        _stats.Add();
        if (zone != SnapZone.Minimize)
            Win32.ForceForeground(_target);
        _armed = false;
        _liveMoved = false;
    }

    private void OnGestureCancelled()
    {
        // Esc (or a 3+ finger abort): in live preview, restore the window to its start.
        if (_livePreview) RestoreLiveOriginal();
        _preview.Hide();
        _chip.Hide();
        _armed = false;
        _liveMoved = false;
    }

    private void OnHoldEngaged()
    {
        if (!_armed) return;
        if (VirtualDesktop.GetLayout(out int cnt, out int idx, out string ld))
        {
            _deskCount = Math.Max(1, cnt);
            _deskIndex = Math.Clamp(idx, 0, _deskCount - 1);
        }
        _chip.ShowDesktopStrip(_deskCount, _deskIndex, null, animateReveal: true);
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
            Win32.ForceForeground(_target);
            _chip.ShowDesktopStrip(_deskCount, _deskIndex, null);
            _stats.Add();
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
        if (Win32.GetWindowRect(_target, out var wr))
        {
            _freeWinX = wr.Left; _freeWinY = wr.Top;
            _freeWinW = Math.Max(MinFreeW, wr.Right - wr.Left);
            _freeWinH = Math.Max(MinFreeH, wr.Bottom - wr.Top);
        }
        var work = _snapper.WorkAreaFor(_target);
        _freeWorkW = Math.Max(1, work.Width);
        _freeWorkH = Math.Max(1, work.Height);

        // The window itself is the live feedback here — no snap/desktop HUD.
        _preview.Hide();
        _chip.Hide();
        Log.Write($"FreeMoveBegan armed pos=({_freeWinX:F0},{_freeWinY:F0}) work={_freeWorkW}x{_freeWorkH}");
    }

    private void OnFreeMoveDelta(double ddx, double ddy, double scale)
    {
        if (!_free || !_armed) return;
        // Map normalized pad travel onto the monitor: a full pad sweep moves the
        // window a full work-area span (touchpad acts as the monitor).
        _freeWinX += ddx * _freeWorkW * FreeMoveScale;
        _freeWinY += ddy * _freeWorkH * FreeMoveScale;

        // Expanding/contracting the hand scales the window about its center, so it
        // grows and shrinks in place while still following the hand's translation.
        if (scale != 1.0)
        {
            double g = Math.Pow(scale, FreeResizeGain);
            double cx = _freeWinX + _freeWinW / 2.0;
            double cy = _freeWinY + _freeWinH / 2.0;
            _freeWinW = Math.Clamp(_freeWinW * g, MinFreeW, _freeWorkW);
            _freeWinH = Math.Clamp(_freeWinH * g, MinFreeH, _freeWorkH);
            _freeWinX = cx - _freeWinW / 2.0;
            _freeWinY = cy - _freeWinH / 2.0;
        }

        // ASYNCWINDOWPOS posts the move to the target window's thread instead of
        // blocking ours waiting for that (possibly heavy) app to repaint, and
        // NOSENDCHANGING skips the synchronous WM_WINDOWPOSCHANGING round-trip.
        // Together they keep the window glued to the finger during fast motion
        // instead of trailing as raw-input frames queue up behind blocked calls.
        Win32.SetWindowPos(_target, IntPtr.Zero,
            (int)Math.Round(_freeWinX), (int)Math.Round(_freeWinY),
            (int)Math.Round(_freeWinW), (int)Math.Round(_freeWinH),
            Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER | Win32.SWP_NOACTIVATE
                | Win32.SWP_ASYNCWINDOWPOS | Win32.SWP_NOSENDCHANGING);
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
                if (_centerEnabled)
                {
                    _snapper.CenterOnMonitor(_target);
                    _stats.Add();
                }
            }
            Win32.ForceForeground(_target);
        }
        _free = false;
        _armed = false;
    }

    private void OnPinchUpdated(bool outward, double progress)
    {
        if (!_armed) return;
        if (outward)
        {
            // Spreading: preview the full-screen target so the user sees it will
            // take the whole monitor before it commits.
            var work = _snapper.WorkAreaFor(_target);
            _preview.ShowZone(work, progress);
            _chip.ShowSnap(SnapZone.Maximize, progress);
        }
        else if (WindowSnapper.IsMaximized(_target) && _hasPreMax && _preMaxHwnd == _target)
        {
            // Drawing together on a window we previously fullscreened: preview the
            // exact rect it will snap back to.
            _preview.ShowZone(_preMaxRect, progress);
            ShowRestorePreviewChip(_preMaxRect);
        }
        else
        {
            // Pinch-in on a window we can't restore: nothing to show.
            _preview.Hide();
            _chip.Hide();
        }
    }

    private void ShowRestorePreviewChip(Win32.RECT rect)
    {
        var work = _snapper.WorkAreaFor(_target);
        if (work.Width <= 0 || work.Height <= 0) return;
        double x0 = Math.Clamp((rect.Left - work.Left) / (double)work.Width, 0, 1);
        double y0 = Math.Clamp((rect.Top - work.Top) / (double)work.Height, 0, 1);
        double x1 = Math.Clamp((rect.Right - work.Left) / (double)work.Width, 0, 1);
        double y1 = Math.Clamp((rect.Bottom - work.Top) / (double)work.Height, 0, 1);
        _chip.ShowFraction(x0, y0, x1, y1, 1);
    }

    private void OnPinchOut()
    {
        // Two fingers spread apart over a titlebar: fullscreen (maximize) the armed
        // window. Native maximize keeps the OS animation, matching the Up swipe.
        _preview.Hide();
        _chip.Hide();
        if (!_armed) return;
        if (!_maximizeEnabled) { _armed = false; return; }

        // Remember the floating rect so a later pinch-in puts it back exactly. Skip
        // if it's already maximized so we never store the full-screen rect.
        if (!WindowSnapper.IsMaximized(_target) && Win32.GetWindowRect(_target, out var r))
        {
            _preMaxRect = r;
            _preMaxHwnd = _target;
            _hasPreMax = true;
        }

        _snapper.Apply(_target, SnapZone.Maximize);
        Win32.ForceForeground(_target);
        _stats.Add();
        Log.Write("PinchOut -> Maximize");
        _armed = false;
    }

    private void OnPinchIn()
    {
        // Two fingers drawn together over a titlebar: restore a fullscreened window.
        _preview.Hide();
        _chip.Hide();
        if (!_armed) return;
        if (!WindowSnapper.IsMaximized(_target)) return; // nothing to restore

        if (_hasPreMax && _preMaxHwnd == _target)
        {
            _snapper.RestoreToRect(_target, _preMaxRect); // back to where it was
            _hasPreMax = false;
        }
        else
        {
            _snapper.RestoreWindow(_target); // OS-maximized window: native restore
        }
        Win32.ForceForeground(_target);
        _stats.Add();
        Log.Write("PinchIn -> Restore");
        _armed = false;
    }

    private void OnHotkey(SnapZone zone)
    {
        IntPtr h = _snapper.WindowUnderCursor();
        bool man = _snapper.IsManageable(h);
        Log.Write($"OnHotkey zone={zone} hwnd=0x{h.ToInt64():X} manageable={man} title='{Win32.GetWindowTitle(h)}'");
        if (!man) return;
        _snapper.Apply(h, zone);
        if (zone != SnapZone.None) _stats.Add();
        if (zone != SnapZone.Minimize)
            Win32.ForceForeground(h);
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
        _stats.Dispose();
        _window.Dispose();
    }
}
