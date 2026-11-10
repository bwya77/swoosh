using System.Text.Json;
using System.Text.Json.Serialization;
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
        Assert.Equal(0, s.GridSpacing);
        Assert.Equal(0.9, s.CancelTimeoutSeconds, 5);
        Assert.False(s.LivePreview);
        Assert.False(s.LaunchAtLogin);
        Assert.Equal("#0A84FF", s.OverlayColor);
        Assert.Equal(AppCompatibilityMode.Exclude, s.AppCompatibilityMode);
        Assert.Equal(GridModifier.Ctrl, s.AppCompatibilityModifier);
        Assert.Empty(s.AppCompatibilityProcessNames);
    }

    [Fact]
    public void Json_RoundTrip_Preserves_Values()
    {
        var original = new AppSettings
        {
            GesturesEnabled = false,
            GridSpacing = 7,
            CancelTimeoutSeconds = 1.5,
            LivePreview = true,
            LaunchAtLogin = true,
            OverlayUseAccent = false,
            OverlayColor = "#FF2D55",
            MonitorMoveModifier = GridModifier.Shift,
            AppCompatibilityMode = AppCompatibilityMode.RequireModifier,
            AppCompatibilityModifier = GridModifier.Alt,
            AppCompatibilityProcessNames = ["firefox.exe", "brave.exe"],
        };

        var json = JsonSerializer.Serialize(original);
        var copy = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.False(copy.GesturesEnabled);
        Assert.Equal(7, copy.GridSpacing);
        Assert.Equal(1.5, copy.CancelTimeoutSeconds, 5);
        Assert.True(copy.LivePreview);
        Assert.True(copy.LaunchAtLogin);
        Assert.False(copy.OverlayUseAccent);
        Assert.Equal("#FF2D55", copy.OverlayColor);
        Assert.Equal(GridModifier.Shift, copy.MonitorMoveModifier);
        Assert.Equal(AppCompatibilityMode.RequireModifier, copy.AppCompatibilityMode);
        Assert.Equal(GridModifier.Alt, copy.AppCompatibilityModifier);
        Assert.Equal(new[] { "firefox.exe", "brave.exe" }, copy.AppCompatibilityProcessNames);
    }

    [Fact]
    public void Clone_Is_Independent()
    {
        var s = new AppSettings { GridSpacing = 3, AppCompatibilityProcessNames = ["firefox.exe"] };
        var c = s.Clone();
        c.GridSpacing = 9;
        c.AppCompatibilityProcessNames.Add("brave.exe");
        Assert.Equal(3, s.GridSpacing);   // mutating the clone doesn't touch the original
        Assert.Equal(9, c.GridSpacing);
        Assert.Equal(new[] { "firefox.exe" }, s.AppCompatibilityProcessNames);
        Assert.Equal(new[] { "firefox.exe", "brave.exe" }, c.AppCompatibilityProcessNames);
    }

    [Theory]
    [InlineData(GridModifier.Ctrl)]
    [InlineData(GridModifier.Alt)]
    [InlineData(GridModifier.Shift)]
    [InlineData(null)]
    public void Mouse_Hud_RoundTrip_Preserves_Selected_Activation(GridModifier? modifier)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var settings = new AppSettings
        {
            MouseMiddleButtonHudEnabled = true,
            MouseHudModifier = modifier,
        };

        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, options), options)!;

        Assert.True(copy.MouseMiddleButtonHudEnabled);
        Assert.Equal(modifier, copy.MouseHudModifier);
    }

    [Fact]
    public void Existing_Mouse_Settings_Keep_Middle_Button_Without_Modifier()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"MouseMiddleButtonHudEnabled":true}""")!;

        Assert.True(settings.MouseMiddleButtonHudEnabled);
        Assert.Null(settings.MouseHudModifier);
    }

    [Fact]
    public void Legacy_Thirds_Settings_Are_Ignored_Without_Losing_Mouse_Activation()
    {
        const string json = """
            {
                "GridModifierEnabled": true,
                "GridModifier": "Shift",
                "Sensitivity": 0.8,
                "MouseMiddleButtonHudEnabled": true,
                "MouseHudModifier": "Ctrl",
                "MonitorMoveModifier": "Alt",
                "GridSpacing": 4
            }
            """;
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        var settings = JsonSerializer.Deserialize<AppSettings>(json, options)!;

        Assert.True(settings.MouseMiddleButtonHudEnabled);
        Assert.Equal(GridModifier.Ctrl, settings.MouseHudModifier);
        Assert.Equal(GridModifier.Alt, settings.MonitorMoveModifier);
        Assert.Equal(4, settings.GridSpacing);
        var saved = JsonSerializer.Serialize(settings, options);
        Assert.DoesNotContain("\"GridModifierEnabled\"", saved);
        Assert.DoesNotContain("\"GridModifier\"", saved);
        Assert.DoesNotContain("\"Sensitivity\"", saved);
    }

    [Fact]
    public void AppCompatibility_Process_List_Normalizes_User_Input()
    {
        var parsed = AppCompatibility.ParseProcessList(" Firefox ; brave.exe\r\n\"C:\\Apps\\Vivaldi.exe\"\nfirefox.exe ");

        Assert.Equal(new[] { "firefox.exe", "brave.exe", "vivaldi.exe" }, parsed);
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
