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

/// <summary>On-screen size of the gesture HUD.</summary>
public enum HudSize
{
    Normal,
    Large,
}

/// <summary>What a downward swipe does (when the down gesture is enabled).</summary>
public enum SwipeDownMode
{
    /// <summary>Minimize the window (the default, classic behavior).</summary>
    Minimize,
    /// <summary>Close the window.</summary>
    Close,
    /// <summary>Show a chooser HUD; lean left to minimize or right to close.</summary>
    Choose,
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

    /// <summary>What a downward swipe does when the down gesture is enabled: minimize (default),
    /// close, or show a chooser to pick between minimize and close.</summary>
    public SwipeDownMode SwipeDownAction { get; set; } = SwipeDownMode.Minimize;

    /// <summary>How deliberate a downward swipe must be before the down-action (close/choose)
    /// engages, as a fraction of touchpad travel. Higher means the user must pull further down,
    /// so incidental downward motion during other gestures won't trigger minimize/close.
    /// Clamped to a sane range at apply time.</summary>
    public double SwipeDownThreshold { get; set; } = 0.15;

    /// <summary>Master switch for all five-finger gestures: five-finger drag to free-move, the
    /// expand/pinch free resize, and the five-finger tap to center. When off, five-finger touches
    /// are ignored. The five-finger tap-to-center also requires <see cref="CenterEnabled"/>.</summary>
    public bool FiveFingerEnabled { get; set; } = true;

    /// <summary>Five-finger tap to center the window without resizing it.</summary>
    public bool CenterEnabled { get; set; } = true;

    /// <summary>When enabled, spreading two fingers apart horizontally resizes the window's width
    /// only (move together to shrink). Replaces the two-finger pinch-to-maximize while either this
    /// or <see cref="ResizeVerticalEnabled"/> is on; maximize stays available via swipe-up.</summary>
    public bool ResizeHorizontalEnabled { get; set; } = false;

    /// <summary>When enabled, spreading two fingers apart vertically resizes the window's height
    /// only (move together to shrink). Replaces the two-finger pinch-to-maximize while either this
    /// or <see cref="ResizeHorizontalEnabled"/> is on; maximize stays available via swipe-up.</summary>
    public bool ResizeVerticalEnabled { get; set; } = false;

    /// <summary>Show the polished touchpad demo overlay (presentation mode for recordings).</summary>
    public bool DemoOverlay { get; set; } = false;

    /// <summary>Apply firmware phantom-contact rejection (default on). A diagnostic kill-switch:
    /// turn it off only if a touchpad's gestures behave erratically, to test whether the
    /// rejection heuristics are the cause.</summary>
    public bool PhantomRejection { get; set; } = true;

    /// <summary>Whether the first-run welcome/tutorial has been completed. Shown once on the
    /// first launch; can be replayed from the tray menu.</summary>
    public bool OnboardingCompleted { get; set; } = false;

    /// <summary>Animate window moves with an ease-out glide instead of snapping instantly.</summary>
    public bool AnimateSnaps { get; set; } = true;

    /// <summary>How long the window-move glide takes, in seconds (when AnimateSnaps is on). The
    /// HUD fill and preview ghost glides match this so they feel in sync. Clamped at apply time.</summary>
    public double SnapAnimationSeconds { get; set; } = 0.22;

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

    /// <summary>On-screen size of the gesture HUD. Large renders it bigger than the
    /// default Normal size.</summary>
    public HudSize HudSize { get; set; } = HudSize.Normal;

    /// <summary>Start Swoosh automatically when you sign in to Windows.</summary>
    public bool LaunchAtLogin { get; set; } = false;

    /// <summary>Custom overlay highlight color (hex #RRGGBB) used when not following the accent.</summary>
    public string OverlayColor { get; set; } = "#0A84FF";

    /// <summary>Gap in logical pixels left between a snapped window and the work-area edges
    /// and its neighbours (0 = flush, Swish-style; up to 10).</summary>
    public int GridSpacing { get; set; } = 0;

    /// <summary>Seconds of resting (fingers still) before an in-progress gesture cancels
    /// itself. Pressing Esc cancels immediately. 0 disables the rest-timeout.</summary>
    public double CancelTimeoutSeconds { get; set; } = 0.9;

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

    /// <summary>When true, holding a window then swiping no longer moves it between virtual
    /// desktops; instead the HUD shows your open apps and swiping changes focus to the selected
    /// app so you can work in it (like Alt+Tab driven by the hold-swipe gesture). Off by default.</summary>
    public bool AppSwitchOnHold { get; set; } = false;

    /// <summary>When true, holding then swiping past the last (rightmost) virtual desktop creates a
    /// new desktop and moves the window there, instead of stopping at the edge. Off by default.</summary>
    public bool CreateDesktopOnOverflow { get; set; } = false;

    /// <summary>Seconds two fingers must rest (near-still) before the press-and-hold
    /// virtual-desktop switcher engages and its HUD appears. Range about 0.1 to 1.0s.</summary>
    public double DesktopHoldDelaySeconds { get; set; } = 0.3;

    /// <summary>How long the gesture HUD takes to fade out when a gesture ends, in seconds.
    /// Higher values make the HUD linger and fade more slowly. Range about 0.1 to 1.5s.</summary>
    public double HudFadeOutSeconds { get; set; } = 0.36;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
