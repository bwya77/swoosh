namespace Swoosh.Settings;

/// <summary>Which keyboard modifier switches snapping into Swish-style thirds.</summary>
public enum GridModifier
{
    Shift,
    Ctrl,
    Alt,
}

/// <summary>User-facing, persisted application settings (serialized to JSON).</summary>
public sealed class AppSettings
{
    /// <summary>Master switch for all touchpad gestures.</summary>
    public bool GesturesEnabled { get; set; } = true;

    /// <summary>Show the live touchpad debug overlay on launch.</summary>
    public bool DebugOverlay { get; set; } = false;

    /// <summary>Animate window moves with an ease-out glide instead of snapping instantly.</summary>
    public bool AnimateSnaps { get; set; } = true;

    /// <summary>When true, holding the modifier during a snap swipe targets a column/row third.</summary>
    public bool GridModifierEnabled { get; set; } = true;

    /// <summary>Which modifier key engages thirds snapping (Swish defaults to Shift).</summary>
    public GridModifier GridModifier { get; set; } = GridModifier.Shift;

    /// <summary>How readily a slightly diagonal swipe lands a corner cell, 0 (forgiving)
    /// to 1 (twitchy). Lower values make sideways swipes ignore vertical drift.</summary>
    public double Sensitivity { get; set; } = 0.5;

    /// <summary>Use the current Windows accent color for the snap overlay highlight.</summary>
    public bool OverlayUseAccent { get; set; } = true;

    /// <summary>Custom overlay highlight color (hex #RRGGBB) used when not following the accent.</summary>
    public string OverlayColor { get; set; } = "#0A84FF";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
