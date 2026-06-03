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

    /// <summary>How long two fingers must rest (near-still) to engage hold mode.</summary>
    public long HoldDelayMs { get; set; } = 320;

    /// <summary>Max centroid travel allowed during the dwell to still count as a hold.</summary>
    public double HoldRadius { get; set; } = 0.05;

    /// <summary>Horizontal travel (after holding) needed to pick a desktop direction.</summary>
    public double DesktopMoveThreshold { get; set; } = 0.09;

    /// <summary>Simultaneous contacts that engage fine-grained free positioning.</summary>
    public int FreeMoveEngageContacts { get; set; } = 5;

    /// <summary>Contacts that keep free-move alive once engaged (hysteresis for finger flicker).</summary>
    public int FreeMoveKeepContacts { get; set; } = 4;

    /// <summary>Max duration of a five-finger touch for it to count as a tap (ms).</summary>
    public long FiveTapMaxMs { get; set; } = 350;

    /// <summary>Max centroid travel allowed during a five-finger touch for it to still count as a tap.</summary>
    public double FiveTapMaxDist { get; set; } = 0.06;

    public event Action<int>? GestureBegan;
    public event Action<SwipeDirection, double>? GestureUpdated;
    public event Action<SwipeDirection>? GestureCompleted;
    public event Action? GestureCancelled;

    /// <summary>True while the thirds modifier is held: enables fine column/row third
    /// snapping and uses a smaller dead-zone so small adjustments still register.</summary>
    public bool ThirdsMode { get; set; }

    /// <summary>Dead-zone used while <see cref="ThirdsMode"/> is active.</summary>
    public double ThirdsDeadZone { get; set; } = 0.03;

    /// <summary>Raw signed centroid delta (dx, dy) from the swipe start, fired every
    /// two-finger frame. Pad Y grows downward. Lets the controller pick a thirds target
    /// by magnitude rather than just an 8-way direction.</summary>
    public event Action<double, double>? SwipeRaw;

    /// <summary>Fired once when two fingers have rested long enough (hold engaged).</summary>
    public event Action? HoldEngaged;

    /// <summary>Live hold-mode update: current desktop direction (null = none) + progress.</summary>
    public event Action<DesktopDirection?, double>? HoldUpdated;

    /// <summary>Fired on release while held, when a desktop direction was chosen.</summary>
    public event Action<DesktopDirection>? DesktopMove;

    /// <summary>Fired when enough fingers (default 5) land to begin fine-grained free positioning.</summary>
    public event Action? FreeMoveBegan;

    /// <summary>Per-frame normalized centroid delta (dx, dy) while free-moving. Pad Y grows downward.</summary>
    public event Action<double, double>? FreeMoveDelta;

    /// <summary>Fired when the fingers lift and free-move ends. The bool is true when the
    /// touch was a brief, near-still FIVE-finger tap (Swish-style center the window) rather
    /// than an actual move.</summary>
    public event Action<bool>? FreeMoveEnded;

    private bool _tracking;
    private bool _cancelled;
    private double _startX, _startY, _lastX, _lastY;
    private long _startTime;
    private SwipeDirection _currentDir = SwipeDirection.None;

    private bool _hold;
    private bool _holdEligible = true;
    private double _maxDist;
    private double _holdAnchorX;

    private bool _free;
    private double _freeLastX, _freeLastY;
    private bool _freeHasLast;
    private int _freeLastCount;

    // Five-finger tap tracking: a brief, near-still five-finger touch (no real
    // movement) centers the window instead of free-moving it.
    private long _freeStartTime;
    private double _freeStartX, _freeStartY, _freeMaxDist;

    public void Process(TouchFrame frame)
    {
        int down = frame.DownCount;

        // Fine-grained free-move (default 5 fingers): the touchpad becomes an
        // absolute 1:1 proxy for the monitor. Once engaged it owns the gesture
        // stream until the fingers lift, emitting per-frame centroid deltas.
        if (_free)
        {
            if (down >= FreeMoveEngageContacts)
            {
                // Full contact count: track the centroid and move the window.
                var (fx, fy) = Centroid(frame);
                double tdx = fx - _freeStartX, tdy = fy - _freeStartY;
                _freeMaxDist = Math.Max(_freeMaxDist, Math.Sqrt(tdx * tdx + tdy * tdy));
                if (down != _freeLastCount) _freeHasLast = false; // re-anchor across count changes
                _freeLastCount = down;
                if (_freeHasLast)
                    FreeMoveDelta?.Invoke(fx - _freeLastX, fy - _freeLastY);
                _freeLastX = fx; _freeLastY = fy; _freeHasLast = true;
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
                _holdAnchorX = cx;
                _startX = _lastX = cx;
                _startY = _lastY = cy;
                _startTime = frame.TimestampMs;
                GestureBegan?.Invoke(2);
                return;
            }

            _lastX = cx;
            _lastY = cy;
            double dx = cx - _startX;
            double dy = cy - _startY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            _maxDist = Math.Max(_maxDist, dist);
            SwipeRaw?.Invoke(dx, dy);

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
                HoldUpdated?.Invoke(lean, prog);

                // Commit a step once travel crosses the full threshold, then
                // re-anchor so further travel (either way) fires the next step.
                DesktopDirection? step = null;
                if (ddx >= DesktopMoveThreshold) step = DesktopDirection.Right;
                else if (ddx <= -DesktopMoveThreshold) step = DesktopDirection.Left;
                if (step is { } dir)
                {
                    _holdAnchorX = cx;
                    DesktopMove?.Invoke(dir);
                }
            }
            else if (dist >= (ThirdsMode ? ThirdsDeadZone : DeadZone))
            {
                _currentDir = Classify(dx, dy);
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

        if (_cancelled || dur > MaxDurationMs)
        {
            GestureCancelled?.Invoke();
            return;
        }

        // Hold mode: any desktop moves already fired live as the fingers swept,
        // so release just tears down the HUD — no snap, no further animation.
        if (_hold)
        {
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

    private static (double, double) Centroid(TouchFrame f)
    {
        double sx = 0, sy = 0; int n = 0;
        foreach (var c in f.Contacts)
            if (c.TipDown) { sx += c.X; sy += c.Y; n++; }
        return n == 0 ? (0, 0) : (sx / n, sy / n);
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
