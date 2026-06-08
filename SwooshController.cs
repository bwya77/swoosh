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
    private readonly DemoOverlay _demo = new();
    private readonly SwooshStats _stats = new();

    private IntPtr _target;
    private bool _armed;
    private int _deskCount = 2;
    private int _deskIndex;
    private bool _createDesktopOverflow;  // swiping past the last desktop creates a new one

    // App-switcher mode: the hold-swipe gesture cycles focus through open apps instead of
    // moving the window across virtual desktops. Captured at hold-engage, committed on lift.
    private bool _appSwitch;
    private List<Native.AppWindow> _apps = new();
    private int _appStart;
    private int _appSel;
    private Win32.RECT _appTargetRect;  // rect of the window we held over, to place the chosen app into
    private bool _appTargetMax;          // whether that window was maximized

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
    private int _freeWorkX, _freeWorkY;

    /// <summary>How much screen the window covers per unit of pad travel (1.0 = pad spans the monitor).</summary>
    private const double FreeMoveScale = 1.0;

    /// <summary>Amplifies the raw hand-spread ratio so a modest expand/contract makes a
    /// noticeable size change.</summary>
    private const double FreeResizeGain = 1.6;

    /// <summary>Smallest window the five-finger resize will shrink to (physical pixels).</summary>
    private const int MinFreeW = 260, MinFreeH = 180;

    // Two-finger axis-constrained resize state (horizontal width-only or vertical height-only).
    private bool _axisResize;
    private bool _axisHorizontal;
    private double _arWinX, _arWinY, _arWinW, _arWinH;
    private int _arWorkX, _arWorkY, _arWorkW = 1, _arWorkH = 1;
    /// <summary>Amplifies the raw two-finger gap ratio so a modest spread makes a clear size change.</summary>
    private const double AxisResizeGain = 1.5;

    public bool GesturesEnabled { get; set; } = true;

    // Live preview: when on, the real window moves to the target zone as you swipe
    // (instead of the translucent overlay). The original rect is captured at gesture
    // start so Esc can restore it.
    private bool _livePreview;
    private Win32.RECT _liveOrigRect;
    private bool _liveWasMax;
    private bool _targetWasMax;  // the armed window was maximized at gesture start (swipe up restores it)
    private bool _maxRestored;   // we already live-restored a maximized window this gesture
    private long _armedTick;     // when the current two-finger gesture armed (for the HUD-show guard)
    // Briefly suppress the at-rest snap chip after a gesture arms, so placing five fingers (which
    // lands two first, then three-to-five) doesn't flash the two-finger HUD before free-move begins.
    private const long HudArmGuardMs = 110;
    private bool _liveMoved;
    private SnapZone _liveZone = SnapZone.None;

    // Move cursor: when on, capture the cursor's fractional spot within the window at
    // gesture start, then move the cursor to the same fraction of the window's new rect
    // after a snap, so the grab point follows the window.
    private bool _moveCursor;
    private double _curFracX, _curFracY;
    private bool _haveCurFrac;

    // Preview destination desktop: when on, the virtual-desktop strip HUD highlights the
    // neighbour you are leaning toward (where the window will land) instead of the current
    // desktop, then settles on the destination once the move completes.
    private bool _previewDeskDest;

    // Per-gesture enable flags (Swish-style: each snap gesture can be turned off
    // individually from the Snapping settings). Default on.
    private bool _maximizeEnabled = true;
    private bool _halvesEnabled = true;
    private bool _quartersEnabled = true;
    private bool _minimizeEnabled = true;
    private bool _centerEnabled = true;

    // Swipe-down action: minimize (classic), close, or a chooser HUD that lets the user lean
    // left (minimize) or right (close). When the mode is Close/Choose, a down swipe latches into
    // a dedicated down-action handler instead of the normal minimize/snap path.
    private SwipeDownMode _swipeDownMode = SwipeDownMode.Minimize;
    private bool _downLatched;   // a down gesture has engaged the down-action handler
    private bool _downEngaged;   // the chooser/close HUD is currently shown (swipe past dead-zone)
    private bool _downPickClose; // current pick in the chooser: true = close, false = minimize
    private double _downPeakY;   // deepest (most positive) downward vector reached while latched
    private double _downMaxY;    // deepest downward vector reached this gesture (all modes; gates the commit)
    private bool _downRetracting; // chooser is playing its retract-up animation; suppress idle redraws
    private long _retractUntilTick; // suppress redraws until the retract animation has played
    // Prior-snap restore: if the user dwelled on a snap zone before dipping into the chooser,
    // cancelling re-seeds the gesture at that zone so the HUD returns to it and they can keep
    // swiping elsewhere. Detected by dwell time, which reliably separates a deliberate aim from the
    // transient diagonal of a near-vertical summon swipe.
    private SwipeDirection _priorDir = SwipeDirection.None; // current aimed non-minimize direction
    private long _priorDirSinceTick;                        // when the current prior direction began
    private SwipeDirection _restoreDir = SwipeDirection.None; // direction captured at latch (dwell met)
    private const long PriorDwellMs = 150;                  // dwell that marks a deliberate prior aim
    private const double ChooseBand = 0.045; // horizontal lean needed to pick Close
    private const double DownReverseBand = 0.05; // upward retrace from the peak that cancels the down-action
    // How far down (fraction of pad travel) a swipe must reach before the down-action engages.
    // User-tunable so close/minimize only fire on a deliberate pull. Clamped in ApplySettings.
    private double _downThreshold = 0.15;

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
        _gestures.AxisResizeH = s.ResizeHorizontalEnabled;
        _gestures.AxisResizeV = s.ResizeVerticalEnabled;
        _swipeDownMode = s.SwipeDownAction;
        _downThreshold = Math.Clamp(s.SwipeDownThreshold, 0.02, 0.30);
        _snapper.AnimateSnaps = s.AnimateSnaps;
        double snapMs = Math.Clamp(s.SnapAnimationSeconds, 0.05, 0.5) * 1000;
        _snapper.AnimationMs = snapMs;
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
        _chip.ApplyAppearance(s.AnimateSnaps, s.OverlayUseAccent, s.OverlayColor, s.HudBackground, s.HudSize, s.HudFadeOutSeconds, snapMs);
        _preview.ApplyAppearance(s.AnimateSnaps, s.OverlayUseAccent, s.OverlayColor, snapMs);
        _demo.SetAccent(AccentColors.Resolve(s.OverlayUseAccent, s.OverlayColor));
        _demo.SetVisible(s.DemoOverlay);
        _touchpad.PhantomRejection = s.PhantomRejection;
        _livePreview = s.LivePreview;
        _moveCursor = s.MoveCursor;
        _previewDeskDest = s.PreviewDesktopDestination;
        _createDesktopOverflow = s.CreateDesktopOnOverflow;
        _appSwitch = s.AppSwitchOnHold;
        // App switching focuses on lift only (never live, which would steal focus mid-swipe), so
        // force commit-on-release whenever app mode is on; otherwise follow the desktop preview pref.
        _gestures.DesktopMoveOnRelease = s.AppSwitchOnHold || s.PreviewDesktopDestination;
        _gestures.HoldDelayMs = (long)Math.Round(Math.Clamp(s.DesktopHoldDelaySeconds, 0.1, 1.0) * 1000);
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
        _gestures.DesktopHoldCommit += OnDesktopHoldCommit;
        _gestures.MonitorMoveUpdated += OnMonitorMoveUpdated;
        _gestures.MonitorMove += OnMonitorMove;
        _gestures.FreeMoveBegan += OnFreeMoveBegan;
        _gestures.FreeMoveDelta += OnFreeMoveDelta;
        _gestures.FreeMoveEnded += OnFreeMoveEnded;
        _gestures.PinchOut += OnPinchOut;
        _gestures.PinchIn += OnPinchIn;
        _gestures.PinchUpdated += OnPinchUpdated;
        _gestures.AxisResizeBegan += OnAxisResizeBegan;
        _gestures.AxisResizeDelta += OnAxisResizeDelta;
        _gestures.AxisResizeEnded += OnAxisResizeEnded;
        _hotkeys.Triggered += OnHotkey;
    }

    private void OnFrame(TouchFrame frame)
    {
        _demo.Render(frame);
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
        _armedTick = Environment.TickCount64;
        _targetWasMax = _armed && WindowSnapper.IsMaximized(_target);
        _maxRestored = false;
        _liveMoved = false;
        _liveZone = SnapZone.None;
        _downLatched = false;
        _downEngaged = false;
        _downPickClose = false;
        _downPeakY = 0;
        _downMaxY = 0;
        _downRetracting = false;
        _retractUntilTick = 0;
        _priorDir = SwipeDirection.None;
        _priorDirSinceTick = 0;
        _restoreDir = SwipeDirection.None;

        // Capture where the cursor sits inside the window (as a fraction), so a snap can
        // move the cursor to the same relative spot in the window's new position.
        _haveCurFrac = false;
        if (_armed && _moveCursor &&
            Win32.GetCursorPos(out var cp) && Win32.GetWindowRect(_target, out var wr) &&
            wr.Width > 0 && wr.Height > 0)
        {
            _curFracX = Math.Clamp((cp.X - wr.Left) / (double)wr.Width, 0, 1);
            _curFracY = Math.Clamp((cp.Y - wr.Top) / (double)wr.Height, 0, 1);
            _haveCurFrac = true;
        }

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
                // The at-rest chip is shown by OnGestureUpdated after the brief arm guard, so a
                // five-finger landing (which begins as two fingers) doesn't flash it first.
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
                _demo.SetCaption("Move to display");
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

        // Track the deepest downward travel this gesture; it gates both the live preview and the
        // commit-on-release for a down swipe, so the deliberateness threshold is meaningful.
        _downMaxY = Math.Max(_downMaxY, _lastVecY);

        // While the chooser is retracting after a cancel, suppress all redraws so the slide-up/fade
        // animation can play. Once it has played, resume normal handling — which, for a restore,
        // shows the re-seeded prior zone and lets the user keep swiping elsewhere.
        if (_downRetracting)
        {
            if (Environment.TickCount64 < _retractUntilTick) return;
            _downRetracting = false;
        }

        // Escape the down-action by reversing upward. We detect a reversal from the deepest point
        // reached (not an absolute position relative to the gesture start): after a deep down-pull
        // the finger sits near the bottom of the pad, so requiring it to travel back above the
        // start is often physically impossible. On a modest upward retrace from the peak we cancel
        // the chooser, retract it with the same animation as the downward emerge (slide up + fade),
        // and re-baseline the gesture at the current point so the cancel motion is not read as a
        // fresh maximize and the user can keep gesturing.
        if (_downLatched)
        {
            _downPeakY = Math.Max(_downPeakY, _lastVecY);
            if (_lastVecY < _downPeakY - DownReverseBand)
            {
                _downLatched = false;
                _downEngaged = false;
                _downPeakY = 0;
                // Retract the chooser with the slide-up + fade animation, suppressing redraws until
                // it has played. If the user had dwelled on a real snap before dipping down, re-seed
                // the gesture at that zone so the HUD returns to it and they can keep swiping
                // elsewhere; otherwise re-baseline to neutral so the up motion is not a maximize.
                _downRetracting = true;
                _retractUntilTick = Environment.TickCount64 + 320;
                _chip.RetractChooserUp();
                if (_restoreDir != SwipeDirection.None)
                {
                    _gestures.RebaselineSeed(_restoreDir);
                }
                else
                {
                    _gestures.Rebaseline();
                    _preview.Hide();
                }
                return;
            }
        }

        bool downMode = _minimizeEnabled && _swipeDownMode != SwipeDownMode.Minimize;

        if (dir == SwipeDirection.None || progress <= 0)
        {
            // While the chooser is retracting after a cancel, let that animation finish: don't
            // redraw the chip on idle frames (that would collapse the chooser mid-animation).
            if (_downRetracting) return;

            // Retreated below the dead-zone. If we were showing the down-action chooser,
            // disengage it (but stay latched so a renewed pull re-engages) and commit nothing.
            if (_downLatched)
            {
                _downEngaged = false;
                _preview.Hide();
                _chip.Hide();
                return;
            }
            // Arm guard: for the first moment after a gesture begins, don't paint the at-rest chip.
            // This lets a five-finger gesture (which lands as two fingers first) take over without
            // flashing the two-finger HUD. A real swipe is unaffected (it has a direction/progress).
            if (Environment.TickCount64 - _armedTick < HudArmGuardMs)
            {
                _preview.Hide();
                return;
            }
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

        // Deliberateness gate: require a real downward pull before a down swipe does anything,
        // whatever the swipe-down action is (minimize, close, or the chooser). Sticky on the deepest
        // travel so far, so once you have pulled past the threshold it stays engaged. A shallow or
        // incidental down motion is ignored entirely (no preview, no commit), and the commit-on-
        // release is gated the same way below. The threshold is user-tunable; an already-latched
        // chooser gesture bypasses it. (Down is positive; up is negative.)
        if (_minimizeEnabled && !_downLatched && zone == SnapZone.Minimize && _downMaxY < _downThreshold)
        {
            _preview.Hide();
            _chip.ShowSnap(SnapZone.None, 0);
            return;
        }

        // Down-action: a downward swipe under Close/Choose mode. Once latched we own the rest of
        // the gesture, because leaning to pick Close would otherwise reclassify as a quarter snap.
        if (downMode && (_downLatched || zone == SnapZone.Minimize))
        {
            // Capture the prior direction once, when first engaging: restore it on cancel only if the
            // user dwelled on it (a brief pause on the preview) before dipping down, which separates a
            // deliberate aim from the transient diagonal of a near-vertical summon swipe.
            if (!_downLatched)
            {
                long dwell = Environment.TickCount64 - _priorDirSinceTick;
                _restoreDir = (_priorDir != SwipeDirection.None && dwell >= PriorDwellMs)
                    ? _priorDir : SwipeDirection.None;
            }
            HandleDownAction();
            return;
        }

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

        // Swipe up on an already-maximized window restores it (like double-clicking the title
        // bar), rather than re-maximizing. The decision is fixed for the gesture (captured at the
        // start), so it can be driven live without flicker.
        bool restoreInsteadOfMax = zone == SnapZone.Maximize && _targetWasMax;
        bool showRestore = zone == SnapZone.Maximize && (_targetWasMax || _maxRestored);

        _demo.SetCaption(showRestore ? "Restore" : ZoneCaption(zone));

        // Track dwell on the currently-aimed non-minimize direction so cancelling the chooser later
        // can restore it (only when it was dwelled on, not merely passed through).
        if (!_downLatched && zone != SnapZone.Minimize)
        {
            if (dir != _priorDir)
            {
                _priorDir = dir;
                _priorDirSinceTick = Environment.TickCount64;
            }
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
                    if (restoreInsteadOfMax)
                    {
                        _snapper.RestoreFromMaximized(_target);
                        // The window is now a normal, restored window. Clear the maximized flags
                        // and re-baseline to its restored rect so continued swiping snaps cleanly,
                        // a retreat doesn't pop it back to full screen, and the commit doesn't treat
                        // it as a maximize. (Without this the gesture flickers max<->restore.)
                        _targetWasMax = false;
                        _liveWasMax = false;
                        _haveCurFrac = false;
                        _maxRestored = true;
                        Win32.GetWindowRect(_target, out _liveOrigRect);
                    }
                    else _snapper.Apply(_target, zone);
                    _liveMoved = true;
                }
                _liveZone = zone;
            }
            ShowZoneOrRestoreChip(zone, progress, showRestore);
            return;
        }

        var work = _snapper.WorkAreaFor(_target);
        Win32.RECT rect;
        if (restoreInsteadOfMax && WindowSnapper.TryGetRestoreRect(_target, out var restoreRect))
            rect = restoreRect; // preview the pre-maximize size/location, not full screen
        else
            rect = zone == SnapZone.Minimize ? MinimizeHint(work) : WindowSnapper.ZoneRect(work, zone);
        _preview.ShowZone(rect, progress);
        ShowZoneOrRestoreChip(zone, progress, showRestore);
    }

    /// <summary>Show the snap-zone chip, or — when restoring a maximized window — fill the chip to
    /// the window's restored proportions instead of the full-screen maximize fill.</summary>
    private void ShowZoneOrRestoreChip(SnapZone zone, double progress, bool restore)
    {
        if (restore && WindowSnapper.TryGetRestoreRect(_target, out var rr))
        {
            var work = _snapper.WorkAreaFor(_target);
            double w = Math.Max(1, work.Width), h = Math.Max(1, work.Height);
            double x0 = Math.Clamp((rr.Left - work.Left) / w, 0, 1);
            double y0 = Math.Clamp((rr.Top - work.Top) / h, 0, 1);
            double x1 = Math.Clamp((rr.Right - work.Left) / w, 0, 1);
            double y1 = Math.Clamp((rr.Bottom - work.Top) / h, 0, 1);
            _chip.ShowFraction(x0, y0, x1, y1, progress);
        }
        else
        {
            _chip.ShowSnap(zone, progress);
        }
    }

    /// <summary>Drive the down-swipe chooser/close HUD (rendered by the snap HUD so it matches
    /// its look). Latches the gesture so a sideways lean (to pick Close) is not reclassified as a
    /// quarter snap, and tracks the current pick (Choose mode leans left=minimize, right=close).</summary>
    private void HandleDownAction()
    {
        _downLatched = true;
        _downEngaged = true;

        // Make sure no snap glide or live-moved window is in play.
        if (_livePreview && _liveMoved) RestoreLiveOriginal();
        _liveZone = SnapZone.Minimize;
        _preview.Hide();

        if (_swipeDownMode == SwipeDownMode.Close)
        {
            _downPickClose = true;
            _chip.ShowDownChooser(chooseMode: false, closePicked: true);
            _demo.SetCaption("Close");
        }
        else // Choose
        {
            _downPickClose = _lastVecX > ChooseBand;
            _chip.ShowDownChooser(chooseMode: true, closePicked: _downPickClose);
            _demo.SetCaption(_downPickClose ? "Close" : "Minimize");
        }
    }

    /// <summary>Execute the latched down-swipe action on release (minimize or close the
    /// target). No-op if the chooser was retreated out of before lifting.</summary>
    private void CommitDownAction()
    {
        _chip.Hide();
        if (!_downEngaged) return;
        if (_downPickClose)
        {
            Win32.CloseWindow(_target);
            Log.Write($"DownAction: close hwnd=0x{_target.ToInt64():X}");
        }
        else
        {
            _snapper.Apply(_target, SnapZone.Minimize);
            Log.Write($"DownAction: minimize hwnd=0x{_target.ToInt64():X}");
        }
        _stats.Add();
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

        // If the chooser is mid-retract (the user cancelled by swiping up, then lifted before the
        // animation finished), commit the restored prior zone (if any) and let the retract hide
        // itself, instead of snapping the HUD shut here.
        if (_downRetracting)
        {
            _downRetracting = false;
            if (_restoreDir != SwipeDirection.None)
            {
                var rz = MapZone(_restoreDir);
                if (rz != SnapZone.None && rz != SnapZone.Minimize)
                {
                    _snapper.Apply(_target, rz);
                    Win32.ForceForeground(_target);
                }
            }
            _armed = false;
            _liveMoved = false;
            _downLatched = false;
            _downEngaged = false;
            return;
        }

        _chip.Hide();
        if (!_armed) return;

        // A latched down-swipe action (Close/Choose mode) commits its own way.
        if (_downLatched)
        {
            CommitDownAction();
            _armed = false;
            _liveMoved = false;
            _downLatched = false;
            _downEngaged = false;
            return;
        }

        var zone = MapZone(dir);
        if (zone == SnapZone.None)
        {
            // Nothing aimed: in live preview, put the window back where it started.
            if (_livePreview) RestoreLiveOriginal();
            _armed = false;
            _liveMoved = false;
            return;
        }

        // A down swipe that never reached the deliberateness threshold must not commit a minimize,
        // even though it classified as the Minimize zone. Without this the slider would have no
        // effect in Minimize mode (the live gate only suppresses the preview, not the commit).
        if (zone == SnapZone.Minimize && _minimizeEnabled && _downMaxY < _downThreshold)
        {
            if (_livePreview) RestoreLiveOriginal();
            _armed = false;
            _liveMoved = false;
            return;
        }
        // Swipe up on a window that was maximized at gesture start restores it to its previous
        // size and location (like double-clicking the title bar) instead of re-maximizing.
        if (zone == SnapZone.Maximize && _targetWasMax)
        {
            if (!(_livePreview && _liveMoved && _liveZone == zone))
                _snapper.RestoreFromMaximized(_target);
            _stats.Add();
            _demo.SetCaption("Restore");
            Win32.ForceForeground(_target);
            _armed = false;
            _liveMoved = false;
            _haveCurFrac = false;
            return;
        }

        // Live preview already glided the window to this zone during the swipe.
        // Re-applying here restarts the glide (or fires a redundant SetWindowPos to
        // the same spot), which makes the target app repaint at the moment of
        // commit. Skip it when the live window is already at this zone; any in-flight
        // glide finishes at the target on its own.
        if (!(_livePreview && _liveMoved && _liveZone == zone))
            _snapper.Apply(_target, zone);
        _stats.Add();
        _demo.SetCaption(ZoneCaption(zone));
        if (zone != SnapZone.Minimize)
        {
            Win32.ForceForeground(_target);
            MoveCursorToZone(zone);
        }
        _armed = false;
        _liveMoved = false;
        _haveCurFrac = false;
    }

    /// <summary>If the move-cursor setting is on, move the cursor to the same fraction of
    /// the snapped window's new rect that it occupied when the gesture started.</summary>
    private void MoveCursorToZone(SnapZone zone)
    {
        if (!_moveCursor || !_haveCurFrac || _target == IntPtr.Zero) return;
        var work = _snapper.WorkAreaFor(_target);
        var rect = WindowSnapper.ZoneRect(work, zone);
        int cx = rect.Left + (int)Math.Round(_curFracX * rect.Width);
        int cy = rect.Top + (int)Math.Round(_curFracY * rect.Height);
        Win32.SetCursorPos(cx, cy);
    }

    private void OnGestureCancelled()
    {
        // Esc (or a 3+ finger abort): in live preview, restore the window to its start.
        if (_livePreview) RestoreLiveOriginal();
        _preview.Hide();
        _chip.Hide();
        _demo.SetCaption(null);
        _armed = false;
        _apps = new();
        _liveMoved = false;
        _downLatched = false;
        _downEngaged = false;
        _downRetracting = false;
    }

    /// <summary>Friendly demo-overlay caption for a snap zone (e.g. "Snap left", "Maximize").</summary>
    private static string? ZoneCaption(SnapZone zone) => zone switch
    {
        SnapZone.LeftHalf => "Snap left",
        SnapZone.RightHalf => "Snap right",
        SnapZone.TopHalf => "Snap top",
        SnapZone.BottomHalf => "Snap bottom",
        SnapZone.TopLeft => "Top left",
        SnapZone.TopRight => "Top right",
        SnapZone.BottomLeft => "Bottom left",
        SnapZone.BottomRight => "Bottom right",
        SnapZone.Maximize => "Maximize",
        SnapZone.Center => "Center",
        SnapZone.Minimize => "Minimize",
        SnapZone.LeftThird => "Left third",
        SnapZone.CenterThird => "Center third",
        SnapZone.RightThird => "Right third",
        SnapZone.LeftTwoThird => "Left two-thirds",
        SnapZone.RightTwoThird => "Right two-thirds",
        SnapZone.TopThird => "Top third",
        SnapZone.CenterRowThird => "Center third",
        SnapZone.BottomThird => "Bottom third",
        SnapZone.TopTwoThird => "Top two-thirds",
        SnapZone.BottomTwoThird => "Bottom two-thirds",
        SnapZone.ThirdTopLeft => "Top-left third",
        SnapZone.ThirdTopRight => "Top-right third",
        SnapZone.ThirdBottomLeft => "Bottom-left third",
        SnapZone.ThirdBottomRight => "Bottom-right third",
        _ => null,
    };

    private void OnHoldEngaged()
    {
        if (!_armed) return;
        if (_appSwitch)
        {
            // Capture the open-app list once at engage. Start the selection on the app after the
            // current foreground one (the window we armed over), so a single step swaps to it,
            // mirroring how Alt+Tab lands on the previous app.
            _apps = Native.WindowList.GetSwitchableWindows();
            if (_apps.Count == 0) { _armed = false; return; }
            int cur = _apps.FindIndex(a => a.Hwnd == _target);
            if (cur < 0) cur = _apps.FindIndex(a => a.Hwnd == Win32.GetForegroundWindow());
            _appStart = cur < 0 ? 0 : cur;
            _appSel = _appStart;
            // Capture the held window's frame so the chosen app can take its exact place (size and
            // location), effectively swapping the new app in over the old one on release.
            Win32.GetWindowRect(_target, out _appTargetRect);
            _appTargetMax = WindowSnapper.IsMaximized(_target);
            _chip.ShowAppStrip(_apps, _appSel, animateReveal: true);
            Log.Write($"HoldEngaged appswitch n={_apps.Count} start={_appStart}");
            return;
        }
        if (VirtualDesktop.GetLayout(out int cnt, out int idx, out string ld))
        {
            _deskCount = Math.Max(1, cnt);
            _deskIndex = Math.Clamp(idx, 0, _deskCount - 1);
        }
        if (_createDesktopOverflow)
        {
            // Reveal the ghost "+" tile alongside the real desktops from the moment the hold
            // engages, so the create-on-overflow affordance is visible before the user swipes.
            _chip.ShowDesktopStrip(_deskCount + 1, _deskIndex, null, animateReveal: true, previewDestination: _previewDeskDest, overflowNewTile: true);
        }
        else
        {
            _chip.ShowDesktopStrip(_deskCount, _deskIndex, null, animateReveal: true, previewDestination: _previewDeskDest);
        }
        Log.Write($"HoldEngaged layout({ld})");
    }

    private void OnHoldUpdated(DesktopDirection? lean, double progress, int aim)
    {
        if (!_armed) return;
        if (_appSwitch)
        {
            if (_apps.Count == 0) return;
            _appSel = Math.Clamp(_appStart + aim, 0, _apps.Count - 1);
            _chip.ShowAppStrip(_apps, _appSel);
            return;
        }
        if (_previewDeskDest)
        {
            // Preview the desktop the window will jump to: aim is the signed number of
            // desktops from the start. With overflow enabled, always render an extra ghost tile
            // (a "+" slot) at the right end so the user can see that swiping past the last desktop
            // will create a new one; aiming onto it lights it up as the destination.
            int raw = _deskIndex + aim;
            if (_createDesktopOverflow)
            {
                int target = Math.Clamp(raw, 0, _deskCount); // _deskCount == the ghost (new) slot
                _chip.ShowDesktopStrip(_deskCount + 1, _deskIndex, null, previewDestination: true, destIndexOverride: target, overflowNewTile: true);
            }
            else
            {
                int target = Math.Clamp(raw, 0, _deskCount - 1);
                _chip.ShowDesktopStrip(_deskCount, _deskIndex, null, previewDestination: true, destIndexOverride: target);
            }
        }
        else if (_createDesktopOverflow)
        {
            // Live-ratchet mode with overflow on: still present the ghost "+" tile so the
            // affordance is visible while the user leans toward the edge.
            int destOverride = lean == DesktopDirection.Right && _deskIndex >= _deskCount - 1 ? _deskCount : -1;
            _chip.ShowDesktopStrip(_deskCount + 1, _deskIndex, destOverride < 0 ? lean : null,
                previewDestination: destOverride >= 0, destIndexOverride: destOverride, overflowNewTile: true);
        }
        else
        {
            _chip.ShowDesktopStrip(_deskCount, _deskIndex, lean);
        }
    }

    private void OnDesktopMove(DesktopDirection dir)
    {
        _preview.Hide();
        if (!_armed) { return; }
        if (_appSwitch) { return; } // app mode commits focus on lift, never live-steps

        // Overflow: swiping right at the last desktop creates a new one (when enabled) and moves
        // the window there, instead of stopping at the edge.
        if (dir == DesktopDirection.Right && _createDesktopOverflow && _deskIndex >= _deskCount - 1)
        {
            bool created = VirtualDesktop.MoveToNewDesktop(_target, _chip.Handle, out string ndiag);
            Log.Write($"DesktopMove new-desktop ok={created} {ndiag}");
            if (created)
            {
                _deskCount += 1;
                _deskIndex = _deskCount - 1;
                Win32.ForceForeground(_target);
                _chip.ShowDesktopStrip(_deskCount + 1, _deskIndex, null, overflowNewTile: true);
                _stats.Add();
                _demo.SetCaption("New desktop \u2192");
            }
            return;
        }

        // Live ratchet: carry the HUD overlay to the new desktop so it stays visible, and
        // keep the gesture armed so the user can step to further desktops without lifting.
        bool ok = VirtualDesktop.MoveAdjacent(_target, dir, _chip.Handle, out string diag);
        Log.Write($"DesktopMove dir={dir} ok={ok} {diag}");
        if (ok)
        {
            // We follow the window, so the current desktop is now the neighbor.
            _deskIndex = Math.Clamp(_deskIndex + (dir == DesktopDirection.Right ? 1 : -1), 0, _deskCount - 1);
            Win32.ForceForeground(_target);
            if (_createDesktopOverflow)
                _chip.ShowDesktopStrip(_deskCount + 1, _deskIndex, null, overflowNewTile: true);
            else
                _chip.ShowDesktopStrip(_deskCount, _deskIndex, null);
            _stats.Add();
            _demo.SetCaption(dir == DesktopDirection.Right ? "Desktop \u2192" : "\u2190 Desktop");
        }
    }

    /// <summary>Commit-on-release jump: send the window to the previewed target desktop
    /// (possibly several desktops away), follow it there, confirm on the HUD, then fade.</summary>
    private void OnDesktopHoldCommit(int aim)
    {
        _preview.Hide();
        if (!_armed) { return; }

        if (_appSwitch)
        {
            int sel = Math.Clamp(_appStart + aim, 0, Math.Max(0, _apps.Count - 1));
            if (_apps.Count > 0 && sel != _appStart)
            {
                IntPtr hwnd = _apps[sel].Hwnd;
                if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
                // Place the chosen app into the held window's slot so it replaces it in-place: same
                // maximized state, or same size and location for a normal window.
                if (_appTargetMax)
                    Win32.ShowWindow(hwnd, Win32.SW_MAXIMIZE);
                else
                    _snapper.RestoreToRect(hwnd, _appTargetRect);
                Win32.ForceForeground(hwnd);
                _stats.Add();
                Log.Write($"AppSwitch commit -> [{sel}] {_apps[sel].Title} max={_appTargetMax}");
            }
            _armed = false;
            _apps = new();
            _chip.Hide();
            return;
        }

        // Overflow: an aim past the last desktop (with the setting on) creates a new desktop and
        // moves the window there. Over-aiming creates exactly one new desktop.
        if (_createDesktopOverflow && _deskIndex + aim > _deskCount - 1)
        {
            bool created = VirtualDesktop.MoveToNewDesktop(_target, _chip.Handle, out string ndiag);
            Log.Write($"DesktopJump new-desktop aim={aim} ok={created} {ndiag}");
            if (created)
            {
                _deskCount += 1;
                _deskIndex = _deskCount - 1;
                Win32.ForceForeground(_target);
                _chip.ShowDesktopStrip(_deskCount, _deskIndex, null, previewDestination: _previewDeskDest);
                _stats.Add();
            }
            _armed = false;
            _chip.Hide();
            return;
        }

        int target = Math.Clamp(_deskIndex + aim, 0, _deskCount - 1);
        int delta = target - _deskIndex;
        if (delta != 0)
        {
            var dir = delta > 0 ? DesktopDirection.Right : DesktopDirection.Left;
            bool ok = VirtualDesktop.MoveBySteps(_target, dir, Math.Abs(delta), _chip.Handle, out string diag);
            Log.Write($"DesktopJump aim={aim} delta={delta} ok={ok} {diag}");
            if (ok)
            {
                _deskIndex = target;
                Win32.ForceForeground(_target);
                _chip.ShowDesktopStrip(_deskCount, _deskIndex, null, previewDestination: _previewDeskDest);
                _stats.Add();
            }
        }
        // The gesture is over (committed on lift); no cancellation event follows.
        _armed = false;
        _chip.Hide();
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
        _freeWorkX = work.Left;
        _freeWorkY = work.Top;

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

            // Keep a growing window within the visible monitor: clamp its edges to the work area
            // so center-scaling near a screen edge can't push the window off-screen.
            _freeWinX = Math.Clamp(_freeWinX, _freeWorkX, _freeWorkX + _freeWorkW - _freeWinW);
            _freeWinY = Math.Clamp(_freeWinY, _freeWorkY, _freeWorkY + _freeWorkH - _freeWinH);
        }

        // For a pure move, post the change asynchronously and skip the changing
        // notification so the window stays glued to the finger during fast motion. When
        // resizing, do a synchronous SetWindowPos with the normal notifications instead:
        // posting an async grow lets the frame expand before the app repaints, which shows
        // black in the newly revealed area (most visible on WinUI windows).
        bool resizing = scale != 1.0;
        uint flags = Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER | Win32.SWP_NOACTIVATE;
        if (!resizing) flags |= Win32.SWP_ASYNCWINDOWPOS | Win32.SWP_NOSENDCHANGING;
        Win32.SetWindowPos(_target, IntPtr.Zero,
            (int)Math.Round(_freeWinX), (int)Math.Round(_freeWinY),
            (int)Math.Round(_freeWinW), (int)Math.Round(_freeWinH),
            flags);
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

    /// <summary>Begin a two-finger axis-constrained resize: arm the target (already armed by the
    /// gesture begin), restore it if maximized, capture its rect and the monitor work area, and
    /// hide the snap HUD. The window itself is the live feedback.</summary>
    private void OnAxisResizeBegan(bool horizontal)
    {
        if (!_armed || _target == IntPtr.Zero) { _axisResize = false; return; }

        long style = Win32.GetWindowLong(_target, Win32.GWL_STYLE);
        if ((style & (Win32.WS_MAXIMIZE | Win32.WS_MINIMIZE)) != 0)
            Win32.ShowWindow(_target, Win32.SW_RESTORE);

        _axisResize = true;
        _axisHorizontal = horizontal;
        if (Win32.GetWindowRect(_target, out var wr))
        {
            _arWinX = wr.Left; _arWinY = wr.Top;
            _arWinW = Math.Max(MinFreeW, wr.Right - wr.Left);
            _arWinH = Math.Max(MinFreeH, wr.Bottom - wr.Top);
        }
        var work = _snapper.WorkAreaFor(_target);
        _arWorkX = work.Left; _arWorkY = work.Top;
        _arWorkW = Math.Max(1, work.Width); _arWorkH = Math.Max(1, work.Height);

        _preview.Hide();
        _chip.Hide();
    }

    /// <summary>Scale only the locked axis (width or height) about the window's center by the
    /// per-frame gap factor, clamped to the min size and the visible monitor.</summary>
    private void OnAxisResizeDelta(double factor, bool horizontal)
    {
        if (!_axisResize || _target == IntPtr.Zero) return;
        double g = Math.Pow(factor, AxisResizeGain);

        if (horizontal)
        {
            double cx = _arWinX + _arWinW / 2.0;
            _arWinW = Math.Clamp(_arWinW * g, MinFreeW, _arWorkW);
            _arWinX = cx - _arWinW / 2.0;
            _arWinX = Math.Clamp(_arWinX, _arWorkX, _arWorkX + _arWorkW - _arWinW);
        }
        else
        {
            double cy = _arWinY + _arWinH / 2.0;
            _arWinH = Math.Clamp(_arWinH * g, MinFreeH, _arWorkH);
            _arWinY = cy - _arWinH / 2.0;
            _arWinY = Math.Clamp(_arWinY, _arWorkY, _arWorkY + _arWorkH - _arWinH);
        }

        // Synchronous resize with normal notifications so the app repaints the revealed area
        // (an async grow shows black until the app catches up, as with the five-finger resize).
        Win32.SetWindowPos(_target, IntPtr.Zero,
            (int)Math.Round(_arWinX), (int)Math.Round(_arWinY),
            (int)Math.Round(_arWinW), (int)Math.Round(_arWinH),
            Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER | Win32.SWP_NOACTIVATE);
    }

    private void OnAxisResizeEnded()
    {
        if (_axisResize && _target != IntPtr.Zero) _stats.Add();
        _axisResize = false;
        _armed = false;
        _preview.Hide();
        _chip.Hide();
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

    public void ToggleDemoOverlay() => _demo.Toggle();

    public void Dispose()
    {
        _touchpad.Dispose();
        _hotkeys.Dispose();
        _preview.Close();
        _chip.Close();
        _demo.Close();
        _stats.Dispose();
        _window.Dispose();
    }
}
