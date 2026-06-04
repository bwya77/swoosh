using System.Text.Json;
using Swoosh.Settings;
using Swoosh.Snapping;
using Xunit;

namespace Swoosh.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Defaults_Are_Stable()
    {
        var s = new AppSettings();
        Assert.True(s.GesturesEnabled);
        Assert.True(s.AnimateSnaps);
        Assert.Equal(0.10, s.Sensitivity, 5);
        Assert.Equal(0, s.GridSpacing);
        Assert.Equal(0.8, s.CancelTimeoutSeconds, 5);
        Assert.False(s.LivePreview);
        Assert.False(s.LaunchAtLogin);
        Assert.Equal("#0A84FF", s.OverlayColor);
    }

    [Fact]
    public void Json_RoundTrip_Preserves_Values()
    {
        var original = new AppSettings
        {
            GesturesEnabled = false,
            Sensitivity = 0.45,
            GridSpacing = 7,
            CancelTimeoutSeconds = 1.5,
            LivePreview = true,
            LaunchAtLogin = true,
            OverlayUseAccent = false,
            OverlayColor = "#FF2D55",
            GridModifier = GridModifier.Ctrl,
            MonitorMoveModifier = GridModifier.Shift,
        };

        var json = JsonSerializer.Serialize(original);
        var copy = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.False(copy.GesturesEnabled);
        Assert.Equal(0.45, copy.Sensitivity, 5);
        Assert.Equal(7, copy.GridSpacing);
        Assert.Equal(1.5, copy.CancelTimeoutSeconds, 5);
        Assert.True(copy.LivePreview);
        Assert.True(copy.LaunchAtLogin);
        Assert.False(copy.OverlayUseAccent);
        Assert.Equal("#FF2D55", copy.OverlayColor);
        Assert.Equal(GridModifier.Ctrl, copy.GridModifier);
        Assert.Equal(GridModifier.Shift, copy.MonitorMoveModifier);
    }

    [Fact]
    public void Clone_Is_Independent()
    {
        var s = new AppSettings { GridSpacing = 3 };
        var c = s.Clone();
        c.GridSpacing = 9;
        Assert.Equal(3, s.GridSpacing);   // mutating the clone doesn't touch the original
        Assert.Equal(9, c.GridSpacing);
    }
}

public class DirectionMapTests
{
    [Theory]
    [InlineData(SwipeDirection.Left, SnapZone.LeftHalf)]
    [InlineData(SwipeDirection.Right, SnapZone.RightHalf)]
    [InlineData(SwipeDirection.Up, SnapZone.Maximize)]
    [InlineData(SwipeDirection.Down, SnapZone.Minimize)]
    [InlineData(SwipeDirection.UpLeft, SnapZone.TopLeft)]
    [InlineData(SwipeDirection.UpRight, SnapZone.TopRight)]
    [InlineData(SwipeDirection.DownLeft, SnapZone.BottomLeft)]
    [InlineData(SwipeDirection.DownRight, SnapZone.BottomRight)]
    [InlineData(SwipeDirection.None, SnapZone.None)]
    public void FromDirection_Maps_Each_Swipe(SwipeDirection dir, SnapZone expected)
    {
        Assert.Equal(expected, SnapZoneMap.FromDirection(dir));
    }
}
