using Swoosh.Native;
using Swoosh.Snapping;
using Xunit;

namespace Swoosh.Tests;

/// <summary>Verifies the pure geometry of <see cref="WindowSnapper.ZoneRect"/>: zones stay
/// inside the work area, tile without gaps or overlaps, and the trailing zone absorbs any
/// rounding remainder so the far edge always reaches the work-area edge.</summary>
public class ZoneRectTests
{
    private static Win32.RECT Work(int w, int h) => new() { Left = 0, Top = 0, Right = w, Bottom = h };

    private static Win32.RECT Zone(Win32.RECT work, SnapZone z) => WindowSnapper.ZoneRect(work, z);

    [Fact]
    public void Maximize_Equals_WorkArea()
    {
        var work = Work(1920, 1080);
        var r = Zone(work, SnapZone.Maximize);
        Assert.Equal(work.Left, r.Left);
        Assert.Equal(work.Top, r.Top);
        Assert.Equal(work.Right, r.Right);
        Assert.Equal(work.Bottom, r.Bottom);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1366, 768)]
    [InlineData(2560, 1440)]
    public void Halves_Tile_The_Width_Exactly(int w, int h)
    {
        var work = Work(w, h);
        var left = Zone(work, SnapZone.LeftHalf);
        var right = Zone(work, SnapZone.RightHalf);

        Assert.Equal(0, left.Left);
        Assert.Equal(left.Right, right.Left);          // no gap, no overlap at the seam
        Assert.Equal(w, right.Right);                  // covers the full width
        Assert.Equal(h, left.Height);                  // full height
        Assert.Equal(h, right.Height);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1366, 769)]
    public void Quarters_Tile_The_Screen(int w, int h)
    {
        var work = Work(w, h);
        var tl = Zone(work, SnapZone.TopLeft);
        var tr = Zone(work, SnapZone.TopRight);
        var bl = Zone(work, SnapZone.BottomLeft);
        var br = Zone(work, SnapZone.BottomRight);

        // Columns meet in the middle, rows meet in the middle, edges reach the corners.
        Assert.Equal(tl.Right, tr.Left);
        Assert.Equal(bl.Right, br.Left);
        Assert.Equal(tl.Bottom, bl.Top);
        Assert.Equal(tr.Bottom, br.Top);
        Assert.Equal(0, tl.Left);
        Assert.Equal(0, tl.Top);
        Assert.Equal(w, br.Right);
        Assert.Equal(h, br.Bottom);

        // Combined area of the four quarters equals the work area (no gaps/overlaps).
        long sum = (long)tl.Width * tl.Height + (long)tr.Width * tr.Height
                 + (long)bl.Width * bl.Height + (long)br.Width * br.Height;
        Assert.Equal((long)w * h, sum);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1366, 768)]   // not divisible by 3: remainder must be absorbed on the right
    public void Thirds_Columns_Tile_Without_Gaps(int w, int h)
    {
        var work = Work(w, h);
        var l = Zone(work, SnapZone.LeftThird);
        var c = Zone(work, SnapZone.CenterThird);
        var r = Zone(work, SnapZone.RightThird);

        Assert.Equal(0, l.Left);
        Assert.Equal(l.Right, c.Left);
        Assert.Equal(c.Right, r.Left);
        Assert.Equal(w, r.Right);           // far edge reaches the work-area edge
        Assert.Equal(h, l.Height);
        Assert.Equal(h, c.Height);
        Assert.Equal(h, r.Height);
    }

    [Theory]
    [InlineData(SnapZone.LeftHalf)]
    [InlineData(SnapZone.RightHalf)]
    [InlineData(SnapZone.TopLeft)]
    [InlineData(SnapZone.BottomRight)]
    [InlineData(SnapZone.CenterThird)]
    [InlineData(SnapZone.ThirdBottomRight)]
    [InlineData(SnapZone.Center)]
    public void Every_Zone_Stays_Within_The_Work_Area(SnapZone zone)
    {
        var work = Work(1920, 1080);
        var r = Zone(work, zone);
        Assert.True(r.Left >= work.Left);
        Assert.True(r.Top >= work.Top);
        Assert.True(r.Right <= work.Right);
        Assert.True(r.Bottom <= work.Bottom);
        Assert.True(r.Width > 0);
        Assert.True(r.Height > 0);
    }
}
