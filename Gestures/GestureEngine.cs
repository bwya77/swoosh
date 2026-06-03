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

    public event Action<int>? GestureBegan;
    public event Action<SwipeDirection, double>? GestureUpdated;
    public event Action<SwipeDirection>? GestureCompleted;
    public event Action? GestureCancelled;

    /// <summary>Fired once when two fingers have rested long enough (hold engaged).</summary>
    public event Action? HoldEngaged;

    /// <summary>Live hold-mode update: current desktop direction (null = none) + progress.</summary>
    public event Action<DesktopDirection?, double>? HoldUpdated;

    /// <summary>Fired on release while held, when a desktop direction was chosen.</summary>
    public event Action<DesktopDirection>? DesktopMove;

    private bool _tracking;
    private bool _cancelled;
    private double _startX, _startY, _lastX, _lastY;
    private long _startTime;
    private SwipeDirection _currentDir = SwipeDirection.None;

    private bool _hold;
    private bool _holdEligible = true;
    private double _maxDist;
    private DesktopDirection? _holdDir;
    private bool _holdMoved;

    public void Process(TouchFrame frame)
    {
        int down = frame.DownCount;

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
                _holdDir = null;
                _holdMoved = false;
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

            // Decide between snap-swipe and press-and-hold. A hold engages only
            // if the fingers stayed near the landing point for the dwell time.
            if (!_hold)
            {
                if (_maxDist >= HoldRadius)
                    _holdEligible = false; // moved too soon → this is a snap swipe
                else if (_holdEligible && frame.TimestampMs - _startTime >= HoldDelayMs)
                {
                    _hold = true;
                    HoldEngaged?.Invoke();
                }
            }

            if (_hold)
            {
                // Hold mode: the first time horizontal travel crosses the
                // threshold, commit the move immediately so the desktop slides
                // over *as you select it*. Release then lands with no further
                // animation. Only one move per hold.
                if (!_holdMoved)
                {
                    DesktopDirection? d = null;
                    if (dx >= DesktopMoveThreshold) d = DesktopDirection.Right;
                    else if (dx <= -DesktopMoveThreshold) d = DesktopDirection.Left;
                    _holdDir = d;
                    HoldUpdated?.Invoke(d, Math.Clamp(Math.Abs(dx) / DesktopMoveThreshold, 0, 1));
                    if (d is { } dir)
                    {
                        _holdMoved = true;
                        DesktopMove?.Invoke(dir);
                    }
                }
            }
            else if (dist >= DeadZone)
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
            // 3+ fingers: not our gesture — bail out.
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

        // Hold mode: the move already fired live as the direction was selected,
        // so release just settles — no further animation. If no direction was
        // ever chosen, treat the release as a cancel.
        if (_hold)
        {
            if (!_holdMoved) GestureCancelled?.Invoke();
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
