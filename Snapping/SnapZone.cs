namespace Swoosh.Snapping;

public enum SnapZone
{
    None,
    LeftHalf, RightHalf, TopHalf, BottomHalf,
    TopLeft, TopRight, BottomLeft, BottomRight,
    Maximize, Center, Minimize,
    // Swish-style thirds, engaged by holding the configured modifier.
    // Columns are full height; rows are full width. Each axis cycles by swipe
    // magnitude: a small push lands two-thirds to that side, a big push one-third,
    // and a tiny push the centered third.
    LeftThird, CenterThird, RightThird,
    LeftTwoThird, RightTwoThird,
    TopThird, CenterRowThird, BottomThird,
    TopTwoThird, BottomTwoThird,
    // Corner 1/3 x 1/3 cells, reached with a diagonal swipe while the modifier is held.
    ThirdTopLeft, ThirdTopRight, ThirdBottomLeft, ThirdBottomRight,
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
