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

/// <summary>
/// Maps a snap zone to the zone(s) that make up the rest of the work area, for the
/// "Snap Assist" overlay: after snapping a window, the empty slot(s) left over are offered to
/// the user's other open windows, mirroring Windows 11's built-in Snap Assist. Halves complement
/// each other; quarters complement the other three quarters; a third/two-third pair complements
/// itself. Zones with no clean geometric complement (the center third/row, corner ninths,
/// maximize, center, minimize) return an empty list, which suppresses the overlay.
/// </summary>
public static class SnapAssistZones
{
    public static IReadOnlyList<SnapZone> ComplementOf(SnapZone zone) => zone switch
    {
        SnapZone.LeftHalf => new[] { SnapZone.RightHalf },
        SnapZone.RightHalf => new[] { SnapZone.LeftHalf },
        SnapZone.TopHalf => new[] { SnapZone.BottomHalf },
        SnapZone.BottomHalf => new[] { SnapZone.TopHalf },

        SnapZone.TopLeft => new[] { SnapZone.TopRight, SnapZone.BottomLeft, SnapZone.BottomRight },
        SnapZone.TopRight => new[] { SnapZone.TopLeft, SnapZone.BottomLeft, SnapZone.BottomRight },
        SnapZone.BottomLeft => new[] { SnapZone.TopLeft, SnapZone.TopRight, SnapZone.BottomRight },
        SnapZone.BottomRight => new[] { SnapZone.TopLeft, SnapZone.TopRight, SnapZone.BottomLeft },

        SnapZone.LeftThird => new[] { SnapZone.RightTwoThird },
        SnapZone.RightThird => new[] { SnapZone.LeftTwoThird },
        SnapZone.LeftTwoThird => new[] { SnapZone.RightThird },
        SnapZone.RightTwoThird => new[] { SnapZone.LeftThird },
        SnapZone.TopThird => new[] { SnapZone.BottomTwoThird },
        SnapZone.BottomThird => new[] { SnapZone.TopTwoThird },
        SnapZone.TopTwoThird => new[] { SnapZone.BottomThird },
        SnapZone.BottomTwoThird => new[] { SnapZone.TopThird },

        _ => Array.Empty<SnapZone>(),
    };
}
