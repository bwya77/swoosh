using Swoosh.Input;
using Swoosh.Snapping;

namespace Swoosh.Gestures;

/// <summary>
/// Recognizes two-finger swipes from a stream of <see cref="TouchFrame"/>s.
/// Emits live updates (for preview) plus a final committed direction.
/// </summary>
public sealed class GestureEngine
{
    /// <summary>Normalized pad distance at which the preview reaches full intensity.</summary>
    public double CommitDistance { get; set; } = 0.12;

    /// <summary>Minimum distance before we lock in a directional intent.</summary>
    public double DeadZone { get; set; } = 0.055;

    /// <summary>Max duration of a drag before it is abandoned, in milliseconds.</summary>
    public long MaxDurationMs { get; set; } = 8000;

    /// <summary>Milliseconds the fingers may rest (near-still) mid-gesture before the gesture
    /// cancels itself. 0 disables the rest-timeout. Esc cancels immediately (see Cancel()).</summary>
    public long IdleCancelMs { get; set; } = 800;

    /// <summary>How long two fingers must rest (near-still) to engage hold mode.</summary>
    public long HoldDelayMs { get; set; } = 200;

    /// <summary>Max centroid travel allowed during the dwell to still count as a hold.</summary>
    public double HoldRadius { get; set; } = 0.05;

    /// <summary>Horizontal travel (after holding) needed to pick a desktop direction.</summary>
    public double DesktopMoveThreshold { get; set; } = 0.09;

    /// <summary>When true, a virtual-desktop hold gesture does NOT switch desktops live as
    /// the fingers sweep. Instead it previews the neighbour being aimed at and commits a
    /// single move to that desktop on release (mirrors how move-to-display commits on lift).</summary>
    public bool DesktopMoveOnRelease { get; set; }

    /// <summary>Simultaneous contacts that engage fine-grained free positioning.</summary>
    public int FreeMoveEngageContacts { get; set; } = 5;

    /// <summary>Contacts that keep free-move alive once engaged (hysteresis for finger flicker).</summary>
    public int FreeMoveKeepContacts { get; set; } = 4;

    /// <summary>Max duration of a five-finger touch for it to count as a tap (ms).</summary>
    public long FiveTapMaxMs { get; set; } = 350;

    /// <summary>Max centroid travel allowed during a five-finger touch for it to still count as a tap.</summary>
    public double FiveTapMaxDist { get; set; } = 0.06;

    /// <summary>Increase in two-finger spread (normalized) needed to trigger a pinch-out fullscreen.</summary>
    public double PinchEngageDelta { get; set; } = 0.10;

    /// <summary>Minimum spread expansion ratio (end/start) for a pinch-out, so the gesture scales with the starting gap.</summary>
    public double PinchEngageRatio { get; set; } = 1.45;

    /// <summary>Max centroid travel allowed during a pinch, so a translating swipe is never read as a pinch.</summary>
    public double PinchMaxCentroidTravel { get; set; } = 0.06;

    /// <summary>Spread change (normalized) at which a pinch starts showing live preview feedback.</summary>
    public double PinchPreviewDelta { get; set; } = 0.035;

    public event Action<int>? GestureBegan;
    public event Action<SwipeDirection, double>? GestureUpdated;
    public event Action<SwipeDirection>? GestureCompleted;
    public event Action? GestureCancelled;

    /// <summary>True while the thirds modifier is held: enables fine column/row third
    /// snapping and uses a smaller dead-zone so small adjustments still register.</summary>
    public bool ThirdsMode { get; set; }

    /// <summary>True while the move-to-display modifier is held: a two-finger swipe
    /// sends the window to the adjacent physical monitor instead of snapping.</summary>
    public bool MonitorMoveMode { get; set; }

    /// <summary>Dead-zone used while <see cref="ThirdsMode"/> is active.</summary>
    public double ThirdsDeadZone { get; set; } = 0.03;

    /// <summary>Raw signed centroid delta (dx, dy) from the swipe start, fired every
    /// two-finger frame. Pad Y grows downward. Lets the controller pick a thirds target
    /// by magnitude rather than just an 8-way direction.</summary>
    public event Action<double, double>? SwipeRaw;

    /// <summary>Fired once when two fingers have rested long enough (hold engaged).</summary>
    public event Action? HoldEngaged;

    /// <summary>Live hold-mode update: leaned desktop direction (null = none), progress, and
    /// the signed number of desktops currently aimed from the hold start (commit-on-release
    /// mode only; 0 = centered, +N to the right, -N to the left).</summary>
    public event Action<DesktopDirection?, double, int>? HoldUpdated;

    /// <summary>Fired on release while held (live ratchet mode), when a desktop direction was chosen.</summary>
    public event Action<DesktopDirection>? DesktopMove;

    /// <summary>Fired on release in commit-on-release mode with the signed number of desktops
    /// aimed (the previewed target). The handler clamps to the available desktops and jumps there.</summary>
    public event Action<int>? DesktopHoldCommit;

    /// <summary>Live move-to-display feedback: the cardinal direction currently aimed at
    /// (null = none yet) plus progress 0..1. Fired while the modifier is held and two
    /// fingers swipe.</summary>
    public event Action<MonitorDirection?, double>? MonitorMoveUpdated;

    /// <summary>Fired on release when the move-to-display modifier was held and a
    /// direction was chosen: send the window to the adjacent monitor.</summary>
    public event Action<MonitorDirection>? MonitorMove;

    /// <summary>Fired when enough fingers (default 5) land to begin fine-grained free positioning.</summary>
    public event Action? FreeMoveBegan;

    /// <summary>Per-frame free-transform while five fingers are down: normalized centroid
    /// delta (dx, dy) to translate the window, plus a multiplicative spread factor
    /// (current finger-spread / previous frame's spread, ~1.0) to grow or shrink it as the
    /// hand expands or contracts. Pad Y grows downward.</summary>
    public event Action<double, double, double>? FreeMoveDelta;

    /// <summary>Fired when the fingers lift and free-move ends. The bool is true when the
    /// touch was a brief, near-still FIVE-finger tap (Swish-style center the window) rather
    /// than an actual move.</summary>
    public event Action<bool>? FreeMoveEnded;

    /// <summary>Fired when two fingers spread apart (pinch-out) over a titlebar: fullscreen the window.</summary>
    public event Action? PinchOut;

    /// <summary>Fired when two fingers draw together (pinch-in) over a titlebar: restore the window.</summary>
    public event Action? PinchIn;

    /// <summary>Live pinch feedback before commit: outward (spreading) plus progress 0..1.</summary>
    public event Action<bool, double>? PinchUpdated;

    private bool _tracking;
    private bool _cancelled;
    private double _startX, _startY, _lastX, _lastY;
    private long _startTime;
    private SwipeDirection _currentDir = SwipeDirection.None;

    // Rest-to-cancel: when the centroid (and finger spread) stay near-still for longer than
    // IdleCancelMs the gesture aborts. _suppressed latches a cancel (Esc or rest-timeout) so
    // a new gesture can't immediately re-engage while the same fingers are still down.
    private long _lastMoveMs;
    private double _idleAnchorX, _idleAnchorY, _idleSpread;
    private bool _suppressed;
    private const double IdleMoveThreshold = 0.008;
    private const double IdleSpreadThreshold = 0.012;

    // Short rolling history of recent centroid samples, used to estimate which way the
    // fingers are travelling *right now*. The 8-way Classify() works off the cumulative
    // vector from the gesture's start, so reversing a swipe (or a constant axis offset
    // baked in at start-capture) drags that vector back across a pure axis and flashes a
    // spurious Up/Down (maximize/minimize) or Left/Right. We only accept a pure cardinal
    // when the live motion is actually along that axis, suppressing the pass-through.
    private readonly double[] _histX = new double[6];
    private readonly double[] _histY = new double[6];
    private int _histCount;

    private bool _hold;
    private bool _holdEligible = true;
    private double _maxDist;
    private double _holdAnchorX;

    // Timestamp of the previous frame while tracking, used to detect input-thread
    // stalls (e.g. first-gesture JIT warm-up on a cold start). Normal Precision
    // Touchpad frames arrive every ~8-16ms; a much larger gap is a hiccup whose dead
    // time must not count toward the hold dwell or rest-to-cancel timers, or a fast
    // first swipe gets misread as a press-and-hold (and then a virtual-desktop move).
    private long _lastFrameMs;
    private const long StallGapMs = 60;
    // In commit-on-release mode, the signed number of desktops currently aimed from the
    // hold-start anchor (previewed target). Committed on lift; 0 means no move.
    private int _holdAimSteps;

    // Move-to-display: latched at gesture start from MonitorMoveMode. While active a
    // two-finger swipe aims a 4-way cardinal direction (shown live in the HUD) and
    // commits the move to the adjacent monitor on finger lift.
    private bool _monitorActive;
    private MonitorDirection? _monitorDir;

    // Pinch-out (two fingers spreading) to fullscreen. Tracked from the gesture's
    // initial finger gap; fires once when the gap grows past the thresholds while
    // the centroid stays roughly fixed.
    private double _startSpread;
    private bool _pinchFired;

    private bool _free;
    private double _freeLastX, _freeLastY;
    private bool _freeHasLast;
    private int _freeLastCount;

    // Five-finger tap tracking: a brief, near-still five-finger touch (no real
    // movement) centers the window instead of free-moving it.
    private long _freeStartTime;
    private double _freeStartX, _freeStartY, _freeMaxDist;

    // Five-finger resize: the mean finger spread (distance from the contact
    // centroid) at the start and on the previous frame. Expanding/contracting the
    // hand scales the window; a small dead-zone around the start spread keeps a
    // pure move or a tap from nudging the size.
    private double _freeStartSpread, _freeLastSpread;

    /// <summary>Spread change from the start gap below which no resize is applied.</summary>
    public double FreeResizeDeadZone { get; set; } = 0.015;

    public void Process(TouchFrame frame)
    {
        int down = frame.DownCount;

        // A latched cancel (Esc or rest-timeout) stays in effect until every finger
        // lifts, so the same touch can't immediately re-arm the gesture it just cancelled.
        if (_suppressed)
        {
            if (down == 0) _suppressed = false;
            return;
        }

        // Fine-grained free-move (default 5 fingers): the touchpad becomes an
        // absolute 1:1 proxy for the monitor. Once engaged it owns the gesture
        // stream until the fingers lift, emitting per-frame centroid deltas.
        if (_free)
        {
            if (down >= FreeMoveEngageContacts)
            {
                // Full contact count: track the centroid and move the window.
                var (fx, fy) = Centroid(frame);
                double curSpread = SpreadN(frame);
                double tdx = fx - _freeStartX, tdy = fy - _freeStartY;
                _freeMaxDist = Math.Max(_freeMaxDist, Math.Sqrt(tdx * tdx + tdy * tdy));
                if (down != _freeLastCount) _freeHasLast = false; // re-anchor across count changes
                _freeLastCount = down;
                if (_freeHasLast)
                {
                    // Only scale once the hand has expanded/contracted past the
                    // dead-zone from where it started, so a translate or tap doesn't
                    // resize. The factor is relative to the previous frame so
                    // re-anchoring after a finger flicker injects no size jump.
                    double factor = Math.Abs(curSpread - _freeStartSpread) < FreeResizeDeadZone || _freeLastSpread < 1e-4
                        ? 1.0
                        : curSpread / _freeLastSpread;
                    FreeMoveDelta?.Invoke(fx - _freeLastX, fy - _freeLastY, factor);
                }
                _freeLastX = fx; _freeLastY = fy; _freeLastSpread = curSpread; _freeHasLast = true;
            }
            else if (down >= FreeMoveKeepContacts)
            {
                // Under the engage count but not yet lifted (e.g. one finger
                // flickered, or fingers are mid-lift): FREEZE — do not move. This
                // stops the window from drifting as fingers peel off on release.
                // Re-anchor so a return to full count won't inject a jump.
                _freeHasLast = false;
                _freeLastCount = down;
            }
            else
            {
                // Decide whether the whole touch was a TAP (brief + near-still)
                // rather than a real move, then tear down. A five-finger tap is
                // the conflict-free "center the window" gesture (no OS gesture
                // claims five-finger taps).
                long fdur = frame.TimestampMs - _freeStartTime;
                bool tap = fdur <= FiveTapMaxMs && _freeMaxDist <= FiveTapMaxDist;
                _free = false;
                _freeHasLast = false;
                FreeMoveEnded?.Invoke(tap);
            }
            return;
        }

        if (down >= FreeMoveEngageContacts)
        {
            // Abandon any in-flight 2-finger snap/hold and switch to free-move.
            if (_tracking) { _tracking = false; GestureCancelled?.Invoke(); }
            _free = true;
            _freeHasLast = false;
            _freeLastCount = down;
            var (sx, sy) = Centroid(frame);
            _freeStartTime = frame.TimestampMs;
            _freeStartX = sx; _freeStartY = sy;
            _freeMaxDist = 0;
            _freeStartSpread = _freeLastSpread = SpreadN(frame);
            FreeMoveBegan?.Invoke();
            return;
        }

        if (down == 2)
        {
            var (cx, cy) = Centroid(frame);
            if (!_tracking)
            {
                _tracking = true;
                _cancelled = false;
                _currentDir = SwipeDirection.None;
                _hold = false;
                _holdEligible = true;
                _maxDist = 0;
                _histCount = 0;
                _holdAnchorX = cx;
                _holdAimSteps = 0;
                _startX = _lastX = cx;
                _startY = _lastY = cy;
                _startTime = frame.TimestampMs;
                _startSpread = Spread(frame);
                _pinchFired = false;
                _monitorActive = MonitorMoveMode;
                _monitorDir = null;
                _lastMoveMs = frame.TimestampMs;
                _lastFrameMs = frame.TimestampMs;
                _idleAnchorX = cx; _idleAnchorY = cy; _idleSpread = _startSpread;
                GestureBegan?.Invoke(2);
                if (_monitorActive) MonitorMoveUpdated?.Invoke(null, 0);
                return;
            }

            _lastX = cx;
            _lastY = cy;
            PushHistory(cx, cy);

            // Cold-start / hiccup guard: if the input thread stalled (for example the
            // first-gesture JIT warm-up), wall-clock time jumps far between frames while
            // the fingers barely moved. Subtract that dead time from the dwell and
            // rest-to-cancel clocks so a stalled fast swipe is not mistaken for a
            // press-and-hold (which would turn it into a virtual-desktop move).
            long frameGap = frame.TimestampMs - _lastFrameMs;
            if (frameGap > StallGapMs)
            {
                _startTime += frameGap;
                _lastMoveMs += frameGap;
            }
            _lastFrameMs = frame.TimestampMs;

            double dx = cx - _startX;
            double dy = cy - _startY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            _maxDist = Math.Max(_maxDist, dist);
            SwipeRaw?.Invoke(dx, dy);

            // Rest-to-cancel: reset the idle clock whenever the fingers move or change
            // spread; if they stay near-still past IdleCancelMs, abandon the gesture and
            // latch the cancel so the HUD can fade out and the touch can't re-arm.
            double idleSpread = Spread(frame);
            bool moved = Math.Abs(cx - _idleAnchorX) >= IdleMoveThreshold
                      || Math.Abs(cy - _idleAnchorY) >= IdleMoveThreshold
                      || Math.Abs(idleSpread - _idleSpread) >= IdleSpreadThreshold;
            if (moved)
            {
                _idleAnchorX = cx; _idleAnchorY = cy; _idleSpread = idleSpread;
                _lastMoveMs = frame.TimestampMs;
            }
            // While the gesture is still an undecided hold candidate (fingers resting to
            // engage the press-and-hold switcher, before it fires), resting is the intended
            // input, not idleness: do not rest-cancel. Otherwise a cancel-timeout shorter
            // than the hold delay would kill the gesture before the hold could engage. Once
            // the hold engages (which resets this clock) or the fingers move into a swipe,
            // rest-to-cancel applies normally.
            else if (IdleCancelMs > 0 && !(_holdEligible && !_hold)
                     && frame.TimestampMs - _lastMoveMs >= IdleCancelMs)
            {
                // Resting "drops" the window: commit the current intent exactly as if the
                // fingers lifted (snap to the previewed zone, or move to the aimed monitor).
                // Esc is the hard cancel. Latch so the same touch can't re-arm afterwards.
                _suppressed = true;
                Finalize(frame.TimestampMs);
                return;
            }

            // Move-to-display mode owns the whole two-finger gesture: aim a 4-way
            // cardinal direction and show it live in the monitor-map HUD. The move
            // commits on finger lift (Finalize), so the user can swing between
            // neighbors before settling. No snap/hold/pinch logic runs here.
            if (_monitorActive)
            {
                MonitorDirection? dir = null;
                if (dist >= DeadZone)
                {
                    dir = Math.Abs(dx) >= Math.Abs(dy)
                        ? (dx >= 0 ? MonitorDirection.Right : MonitorDirection.Left)
                        : (dy >= 0 ? MonitorDirection.Down : MonitorDirection.Up);
                }
                _monitorDir = dir;
                double mprog = Math.Clamp(dist / CommitDistance, 0, 1);
                MonitorMoveUpdated?.Invoke(dir, mprog);
                return;
            }

            // Pinch over a titlebar: two fingers spreading apart fullscreens the
            // window; drawing together restores it (Swish-style). Checked before the
            // hold/snap logic so a still-centroid pinch is never mistaken for a
            // desktop-move hold or a tiny swipe, and gated on small centroid travel so
            // a translating swipe can never trigger it. Live feedback is emitted as the
            // gap changes; the action commits once it passes the distance + ratio gates.
            if (_pinchFired) return;
            double spread = Spread(frame);
            double gain = spread - _startSpread;
            bool centroidFixed = _maxDist <= PinchMaxCentroidTravel;

            if (!_hold && centroidFixed && Math.Abs(gain) >= PinchPreviewDelta)
            {
                _holdEligible = false; // an active gap change is a pinch, not a dwell
                bool outward = gain > 0;
                double prog = Math.Clamp(Math.Abs(gain) / PinchEngageDelta, 0, 1);
                PinchUpdated?.Invoke(outward, prog);

                bool ratioOk = outward
                    ? (_startSpread <= 0.0001 || spread >= _startSpread * PinchEngageRatio)
                    : (_startSpread > 0.0001 && spread <= _startSpread / PinchEngageRatio);

                if (Math.Abs(gain) >= PinchEngageDelta && ratioOk)
                {
                    _pinchFired = true;
                    if (outward) PinchOut?.Invoke();
                    else PinchIn?.Invoke();
                }
                return; // while pinching, never also run the snap/hold logic
            }

            // Decide between snap-swipe and press-and-hold. A hold engages only
            // if the fingers stayed near the landing point for the dwell time.
            if (!_hold)
            {
                if (_maxDist >= HoldRadius)
                    _holdEligible = false; // moved too soon → this is a snap swipe
                else if (_holdEligible && frame.TimestampMs - _startTime >= HoldDelayMs)
                {
                    _hold = true;
                    _holdAnchorX = cx; // moves are measured from the dwell point
                    _lastMoveMs = frame.TimestampMs; // fresh rest-timeout once the strip appears
                    HoldEngaged?.Invoke();
                }
            }

            if (_hold)
            {
                // Hold mode is a repeatable ratchet: every time horizontal travel
                // from the current anchor crosses the threshold we move the window
                // one desktop in that direction and re-anchor at the current
                // position. The user can keep going the same way (further right)
                // or reverse (back left) without lifting — the HUD stays up the
                // whole time. The desktop slides live as each step commits.
                double ddx = cx - _holdAnchorX;
                double prog = Math.Clamp(Math.Abs(ddx) / DesktopMoveThreshold, 0, 1);

                // Visual lean: hint the neighbor you are pushing toward before the
                // step actually commits, so the HUD feels responsive.
                DesktopDirection? lean = null;
                if (ddx > DesktopMoveThreshold * 0.3) lean = DesktopDirection.Right;
                else if (ddx < -DesktopMoveThreshold * 0.3) lean = DesktopDirection.Left;

                // Signed desktops aimed from the hold start: each full threshold of
                // travel targets one more desktop, so a longer swipe can jump several
                // desktops at once (commit-on-release mode).
                int aim = (int)Math.Round(ddx / DesktopMoveThreshold, MidpointRounding.AwayFromZero);
                HoldUpdated?.Invoke(lean, prog, aim);

                if (DesktopMoveOnRelease)
                {
                    // Commit-on-release: don't switch desktops live. Remember how many
                    // desktops are aimed (the previewed target); committed once on lift,
                    // or dropped if the swipe returns to center (aim 0).
                    _holdAimSteps = aim;
                }
                else
                {
                    // Live ratchet: commit a step once travel crosses the full
                    // threshold, then re-anchor so further travel fires the next step.
                    DesktopDirection? step = null;
                    if (ddx >= DesktopMoveThreshold) step = DesktopDirection.Right;
                    else if (ddx <= -DesktopMoveThreshold) step = DesktopDirection.Left;
                    if (step is { } dir)
                    {
                        _holdAnchorX = cx;
                        DesktopMove?.Invoke(dir);
                    }
                }
            }
            else if (dist >= (ThirdsMode ? ThirdsDeadZone : DeadZone))
            {
                _currentDir = Stabilize(_currentDir, Classify(dx, dy));
                double progress = Math.Clamp(dist / CommitDistance, 0, 1);
                GestureUpdated?.Invoke(_currentDir, progress);
            }
            else
            {
                _currentDir = SwipeDirection.None;
                GestureUpdated?.Invoke(SwipeDirection.None, 0);
            }
        }
        else if (down > 2)
        {
            // 3 or 4 fingers (5+ is free-move, handled above): abandon any
            // in-flight 2-finger snap/hold so a stray frame can't commit one.
            CancelIfTracking();
        }
        else // 0 or 1 finger: gesture ended
        {
            if (_tracking)
                Finalize(frame.TimestampMs);
        }
    }

    private void Finalize(long endTime)
    {
        _tracking = false;
        long dur = endTime - _startTime;

        // A pinch-out already fired its action live; release just tears down.
        if (_pinchFired)
        {
            GestureCancelled?.Invoke();
            return;
        }

        // Move-to-display: commit the chosen neighbor on lift, or just tear down
        // the HUD if no direction was aimed.
        if (_monitorActive)
        {
            if (_monitorDir is { } md) MonitorMove?.Invoke(md);
            else GestureCancelled?.Invoke();
            return;
        }

        if (_cancelled || dur > MaxDurationMs)
        {
            GestureCancelled?.Invoke();
            return;
        }

        // Hold mode. In the live ratchet, desktop moves already fired as the fingers
        // swept, so release just tears down the HUD. In commit-on-release mode, the move
        // was deferred: commit the previewed destination now (if one is aimed).
        if (_hold)
        {
            if (DesktopMoveOnRelease && _holdAimSteps != 0)
                DesktopHoldCommit?.Invoke(_holdAimSteps);
            else
                GestureCancelled?.Invoke();
            return;
        }

        // Snap mode: drop wherever the preview currently points.
        if (_currentDir == SwipeDirection.None)
        {
            GestureCancelled?.Invoke();
            return;
        }
        GestureCompleted?.Invoke(_currentDir);
    }

    private void CancelIfTracking()
    {
        if (!_tracking) return;
        _cancelled = true;
        _tracking = false;
        GestureCancelled?.Invoke();
    }

    public void Reset()
    {
        if (_tracking) GestureCancelled?.Invoke();
        _tracking = false;
        if (_free) { _free = false; _freeHasLast = false; FreeMoveEnded?.Invoke(false); }
    }

    /// <summary>Abort the in-progress gesture now (Esc) and latch the cancel so the same
    /// touch can't re-arm until the fingers lift. Tears down whichever mode is active and
    /// raises the matching teardown event so the HUD fades out.</summary>
    public void Cancel()
    {
        bool active = _tracking || _free;
        if (_tracking)
        {
            _cancelled = true;
            _tracking = false;
            GestureCancelled?.Invoke();
        }
        if (_free)
        {
            _free = false;
            _freeHasLast = false;
            FreeMoveEnded?.Invoke(false);
        }
        if (active) _suppressed = true;
    }

    private static (double, double) Centroid(TouchFrame f)
    {
        double sx = 0, sy = 0; int n = 0;
        foreach (var c in f.Contacts)
            if (c.TipDown) { sx += c.X; sy += c.Y; n++; }
        return n == 0 ? (0, 0) : (sx / n, sy / n);
    }

    /// <summary>Normalized distance between the two tip-down contacts (the finger
    /// gap). Returns 0 when fewer than two fingers are down. Order-independent.</summary>
    private static double Spread(TouchFrame f)
    {
        double ax = 0, ay = 0, bx = 0, by = 0; int n = 0;
        foreach (var c in f.Contacts)
        {
            if (!c.TipDown) continue;
            if (n == 0) { ax = c.X; ay = c.Y; }
            else if (n == 1) { bx = c.X; by = c.Y; }
            n++;
        }
        if (n < 2) return 0;
        double dx = bx - ax, dy = by - ay;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Mean distance of the tip-down contacts from their centroid - a finger-spread
    /// metric that works for any contact count (used to scale the window in free-move).
    /// Returns 0 when fewer than two fingers are down.</summary>
    private static double SpreadN(TouchFrame f)
    {
        double sx = 0, sy = 0; int n = 0;
        foreach (var c in f.Contacts)
            if (c.TipDown) { sx += c.X; sy += c.Y; n++; }
        if (n < 2) return 0;
        double cx = sx / n, cy = sy / n, s = 0;
        foreach (var c in f.Contacts)
            if (c.TipDown) { double dx = c.X - cx, dy = c.Y - cy; s += Math.Sqrt(dx * dx + dy * dy); }
        return s / n;
    }

    /// <summary>Push a centroid sample into the rolling motion history.</summary>
    private void PushHistory(double x, double y)
    {
        for (int i = _histX.Length - 1; i > 0; i--)
        {
            _histX[i] = _histX[i - 1];
            _histY[i] = _histY[i - 1];
        }
        _histX[0] = x;
        _histY[0] = y;
        if (_histCount < _histX.Length) _histCount++;
    }

    /// <summary>Net centroid motion over the last few frames (~recent velocity vector).</summary>
    private (double dx, double dy) RecentDelta()
    {
        int last = _histCount - 1;
        if (last <= 0) return (0, 0);
        return (_histX[0] - _histX[last], _histY[0] - _histY[last]);
    }

    /// <summary>Unit-ish (sx, sy) signs for an 8-way direction; screen-space up is +1.</summary>
    private static (int sx, int sy) DirComponents(SwipeDirection d) => d switch
    {
        SwipeDirection.Left => (-1, 0),
        SwipeDirection.Right => (1, 0),
        SwipeDirection.Up => (0, 1),
        SwipeDirection.Down => (0, -1),
        SwipeDirection.UpLeft => (-1, 1),
        SwipeDirection.UpRight => (1, 1),
        SwipeDirection.DownLeft => (-1, -1),
        SwipeDirection.DownRight => (1, -1),
        _ => (0, 0),
    };

    /// <summary>
    /// Suppress a pure-cardinal reading that is only happening because the cumulative
    /// swipe vector is sweeping ACROSS that axis (e.g. dragging right between two corners
    /// momentarily reads as straight-up). A cardinal is accepted only when the live motion
    /// is actually travelling along its axis, so Maximize/Minimize and the halves require a
    /// deliberate swipe in that direction rather than flashing on the way past. Diagonals
    /// and the first direction of a gesture always pass through unchanged.
    /// </summary>
    private SwipeDirection Stabilize(SwipeDirection prev, SwipeDirection cand)
    {
        if (cand == prev || prev == SwipeDirection.None) return cand;
        var (cx, cy) = DirComponents(cand);
        bool candCardinal = (cx == 0) ^ (cy == 0);
        if (!candCardinal) return cand;

        var (rdx, rdy) = RecentDelta();
        double ax = Math.Abs(rdx), ay = Math.Abs(rdy);
        // Up/Down: zeroed axis is X. If we're sliding sideways at least as fast as
        // vertically, this vertical reading is a cross-axis pass-through -> keep prev.
        if (cy != 0 && ax >= ay) return prev;
        // Left/Right: zeroed axis is Y. Suppress while sweeping vertically.
        if (cx != 0 && ay >= ax) return prev;
        return cand;
    }

    /// <summary>Classify a normalized delta into an 8-way direction. Pad Y grows downward.</summary>
    public static SwipeDirection Classify(double dx, double dy)
    {
        // Convert to screen-space where up is positive.
        double up = -dy;
        double angle = Math.Atan2(up, dx) * 180.0 / Math.PI; // -180..180
        if (angle < 0) angle += 360; // 0..360, 0 = right, 90 = up

        return angle switch
        {
            >= 22.5 and < 67.5 => SwipeDirection.UpRight,
            >= 67.5 and < 112.5 => SwipeDirection.Up,
            >= 112.5 and < 157.5 => SwipeDirection.UpLeft,
            >= 157.5 and < 202.5 => SwipeDirection.Left,
            >= 202.5 and < 247.5 => SwipeDirection.DownLeft,
            >= 247.5 and < 292.5 => SwipeDirection.Down,
            >= 292.5 and < 337.5 => SwipeDirection.DownRight,
            _ => SwipeDirection.Right,
        };
    }
}
