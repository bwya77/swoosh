namespace Swoosh.Settings;

/// <summary>Which keyboard modifier switches snapping into Swish-style thirds.</summary>
public enum GridModifier
{
    Shift,
    Ctrl,
    Alt,
}

/// <summary>Backdrop theme for the gesture HUD (snap chip, desktop strip, monitor map).</summary>
public enum HudTheme
{
    Dark,
    Light,
    System,
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
    public double Sensitivity { get; set; } = 0.10;

    /// <summary>Use the current Windows accent color for the snap overlay highlight.</summary>
    public bool OverlayUseAccent { get; set; } = true;

    /// <summary>Backdrop theme for the gesture HUD (snap chip, desktop strip, monitor map).
    /// Dark (default), Light, or System to follow the Windows app light/dark setting. The
    /// highlight color is unchanged.</summary>
    public HudTheme HudBackground { get; set; } = HudTheme.Dark;

    /// <summary>Start Swoosh automatically when you sign in to Windows.</summary>
    public bool LaunchAtLogin { get; set; } = false;

    /// <summary>Custom overlay highlight color (hex #RRGGBB) used when not following the accent.</summary>
    public string OverlayColor { get; set; } = "#0A84FF";

    /// <summary>Gap in logical pixels left between a snapped window and the work-area edges
    /// and its neighbours (0 = flush, Swish-style; up to 10).</summary>
    public int GridSpacing { get; set; } = 0;

    /// <summary>Seconds of resting (fingers still) before an in-progress gesture cancels
    /// itself. Pressing Esc cancels immediately. 0 disables the rest-timeout.</summary>
    public double CancelTimeoutSeconds { get; set; } = 0.8;

    /// <summary>When true, the actual window moves live to the target zone as you swipe
    /// (instead of showing the translucent zone overlay), so you preview on the real app.
    /// Esc restores the window to where it started.</summary>
    public bool LivePreview { get; set; } = false;

    /// <summary>When true, after a snap the cursor follows the window to the same relative
    /// spot it was grabbed at (for example the middle of the titlebar stays under the
    /// cursor in the window's new position). Off by default.</summary>
    public bool MoveCursor { get; set; } = false;

    /// <summary>When true, a virtual-desktop move (hold then swipe) previews the destination
    /// desktop: the strip HUD highlights the desktop the window will jump to as you aim across
    /// it (a longer swipe targets a further desktop), and the move commits on release rather
    /// than switching live as the fingers sweep. On by default.</summary>
    public bool PreviewDesktopDestination { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
