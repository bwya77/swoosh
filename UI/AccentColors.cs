using Color = System.Windows.Media.Color;

namespace Swoosh.UI;

/// <summary>Resolves the highlight color for the snap overlays: either the live
/// Windows accent color (read from the DWM registry key) or a custom hex value.</summary>
internal static class AccentColors
{
    private static readonly Color Default = Color.FromRgb(10, 132, 255);

    public static Color Resolve(bool useAccent, string customHex)
        => (useAccent ? ReadAccent() : ParseHex(customHex)) ?? Default;

    /// <summary>Read the user's Windows accent color from the DWM registry key
    /// (stored as 0xAABBGGRR), or null if unavailable.</summary>
    private static Color? ReadAccent()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int v)
            {
                uint abgr = unchecked((uint)v);
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            }
        }
        catch { /* fall through to default */ }
        return null;
    }

    private static Color? ParseHex(string hex)
    {
        try { return (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }
}
