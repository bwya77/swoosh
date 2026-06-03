namespace Swoosh.Snapping;

public enum SnapZone
{
    None,
    LeftHalf, RightHalf, TopHalf, BottomHalf,
    TopLeft, TopRight, BottomLeft, BottomRight,
    Maximize, Center, Minimize,
    // Thirds (used when chaining a repeated swipe in the same direction)
    LeftThird, RightThird, CenterThird,
}

public static class SnapZoneMap
{
    /// <summary>Maps an 8-way swipe direction to its primary snap zone.</summary>
    public static SnapZone FromDirection(SwipeDirection dir) => dir switch
    {
        SwipeDirection.Left => SnapZone.LeftHalf,
        SwipeDirection.Right => SnapZone.RightHalf,
        SwipeDirection.Up => SnapZone.Maximize,
        SwipeDirection.Down => SnapZone.Minimize,
        SwipeDirection.UpLeft => SnapZone.TopLeft,
        SwipeDirection.UpRight => SnapZone.TopRight,
        SwipeDirection.DownLeft => SnapZone.BottomLeft,
        SwipeDirection.DownRight => SnapZone.BottomRight,
        _ => SnapZone.None,
    };
}

public enum SwipeDirection
{
    None, Left, Right, Up, Down, UpLeft, UpRight, DownLeft, DownRight
}
