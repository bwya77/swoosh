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

    /// <summary>Swipe up (or pinch out) to fill the screen.</summary>
    public bool MaximizeEnabled { get; set; } = true;

    /// <summary>Swipe left or right to snap to that half.</summary>
    public bool HalvesEnabled { get; set; } = true;

    /// <summary>Swipe diagonally to snap to that quarter.</summary>
    public bool QuartersEnabled { get; set; } = true;

    /// <summary>Swipe down to minimize the window.</summary>
    public bool MinimizeEnabled { get; set; } = true;

    /// <summary>Five-finger tap to center the window without resizing it.</summary>
    public bool CenterEnabled { get; set; } = true;

    /// <summary>Show the live touchpad debug overlay on launch.</summary>
    public bool DebugOverlay { get; set; } = false;

    /// <summary>Animate window moves with an ease-out glide instead of snapping instantly.</summary>
    public bool AnimateSnaps { get; set; } = true;

    /// <summary>When true, holding the modifier during a snap swipe targets a column/row third.</summary>
    public bool GridModifierEnabled { get; set; } = true;

    /// <summary>Which modifier key engages thirds snapping (Swish defaults to Shift).</summary>
    public GridModifier GridModifier { get; set; } = GridModifier.Shift;

    /// <summary>When true, holding the move-to-display modifier during a two-finger swipe
    /// sends the window to the adjacent physical monitor instead of snapping.</summary>
    public bool MonitorMoveEnabled { get; set; } = true;

    /// <summary>Which modifier key engages move-to-display (Swish defaults to Alt).</summary>
    public GridModifier MonitorMoveModifier { get; set; } = GridModifier.Alt;

    /// <summary>How readily a slightly diagonal swipe lands a corner cell, 0 (forgiving)
    /// to 1 (twitchy). Lower values make sideways swipes ignore vertical drift.</summary>
    public double Sensitivity { get; set; } = 0.5;

    /// <summary>Use the current Windows accent color for the snap overlay highlight.</summary>
    public bool OverlayUseAccent { get; set; } = true;

    /// <summary>Custom overlay highlight color (hex #RRGGBB) used when not following the accent.</summary>
    public string OverlayColor { get; set; } = "#0A84FF";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
