using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Swoosh.Native;
using Swoosh.Settings;
using Swoosh.Snapping;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Swoosh.UI;

/// <summary>
/// A small "monitor chip" HUD that follows the mouse cursor during a gesture,
/// mirroring macOS Swish. A rounded white square represents the display; the
/// chosen snap zone fills blue edge-to-edge. For virtual-desktop moves it shows
/// two squares (the two desktops) and fills the target solid blue.
///
/// The visual tree is built once and kept alive; gesture updates only re-point
/// the blue fill rectangle (no per-frame rebuild), which keeps it flicker-free.
/// The window is click-through, topmost and positioned in physical pixels so it
/// is DPI-correct on any monitor; internal artwork is stretched via a Viewbox so
/// proportions are scale-independent and zones tile perfectly.
/// </summary>
public sealed class CursorChipOverlay
{
    // ---- Design-space geometry (absolute size derives from cursor-monitor DPI) ----
    private const double Margin = 3;     // space for the stroke / shadow
    private const double Stroke = 2.5;
    private const double Corner = 9;
    private const double ChipH = 60;     // a single chip's height
    private const double Gap = 11;       // gap between the two desktop chips

    private const double SingleChipW = 94;
    private const double SingleCanvasW = SingleChipW + 2 * Margin;     // 100
    private const double DeskW = 84;     // one desktop square in the mini-map strip
    private const double CanvasH = ChipH + 2 * Margin;                 // 66

    private const double BaseHeightPx = 46; // physical chip-window height at 96 DPI

    // Monitor-map (move-to-display) layout: a plus of small display squares.
    private const double MapCellW = 80;
    private const double MapCellH = 54;
    private const double MapGap = 9;
    private const double MapBaseHeightPx = 122; // taller HUD so the 3-row plus is legible
    private const double MapDisabledOpacity = 0.26; // a direction with no monitor reads as dimmed

    private static readonly Brush WhiteEdge = Freeze(new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)));
    private static readonly System.Windows.Media.FontFamily SymbolFont = new("Segoe MDL2 Assets");
    // Near-opaque dark backdrop so the (user-coloured) highlight fill always reads,
    // even with a grey accent over a light or busy wallpaper. A mostly-translucent
    // backdrop blended into bright desktops and washed the contrast out.
    private static readonly Brush ScreenBg = Freeze(new SolidColorBrush(Color.FromArgb(212, 18, 20, 26)));

    // Light HUD theme: near-white backdrop with a soft grey bezel, chosen via settings.
    private static readonly Brush LightEdge = Freeze(new SolidColorBrush(Color.FromArgb(235, 150, 156, 166)));
    private static readonly Brush LightScreenBg = Freeze(new SolidColorBrush(Color.FromArgb(232, 244, 246, 249)));

    // Active backdrop/bezel brushes (swapped by the HUD theme setting). Default dark.
    private HudTheme _hudMode = HudTheme.Dark;
    private bool _lightHud;
    private Brush _screenBg = ScreenBg;
    private Brush _screenEdge = WhiteEdge;
    // Throttle for re-reading the system theme when following it, so a per-frame Show
    // call does not hit the registry every frame.
    private long _lastThemeCheckMs;

    // Highlight color source, remembered so the accent can be re-resolved live when the
    // user changes their Windows accent color (read from the registry, throttled), without
    // restarting. _baseColor is the last resolved highlight color.
    private bool _useAccent = true;
    private string _customHex = "#0A84FF";
    private Color _baseColor = Color.FromRgb(10, 132, 255);
    private long _lastAccentCheckMs;

    // Multiplier on the HUD's physical size (0.65 normal, 1.0 large). The Viewbox scales
    // the whole design to the physical window, so this shrinks everything proportionally.
    private double _hudScale = 0.65;

    private static readonly Brush DefaultSolid = Freeze(new SolidColorBrush(Color.FromArgb(235, 10, 132, 255)));

    // Highlight brushes, recolored from settings (Windows accent or a custom color).
    private Brush _solid = DefaultSolid;
    private Brush _faint = Freeze(new SolidColorBrush(Color.FromArgb(70, 10, 132, 255)));

    // Whether the snap fill glides between zones (mirrors the window-move animation).
    private bool _animate = true;

    // HUD fade-out duration in milliseconds, set from settings. Clamped to a sane range.
    private double _fadeOutMs = 360;

    private static readonly Duration FillDurationDefault = new(TimeSpan.FromMilliseconds(210));
    private Duration _fillDuration = FillDurationDefault;
    private static readonly IEasingFunction FillEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    // Desktop-strip "unfold" reveal: the extra squares slide out from behind the current one.
    private static readonly Duration RevealDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration RevealFadeDuration = new(TimeSpan.FromMilliseconds(190));
    private static readonly IEasingFunction RevealEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private Window? _win;
    private Canvas? _canvas;
    private DispatcherTimer? _hideTimer;
    // Reveal state, decoupled from the live Opacity value so a fade-out in progress can be
    // interrupted by a new reveal. _fadeToken invalidates a pending fade's completion.
    private bool _shown;
    private int _fadeToken;

    // Persistent elements: single-monitor chip (snap mode).
    private Grid? _single;
    private Canvas? _singleInner;
    private Border? _singleFill;

    // Desktop mini-map strip: one square per virtual desktop, rebuilt when the
    // desktop count changes; only the fill brushes change per frame.
    private Grid? _strip;
    private readonly List<Border> _stripFills = new();
    private readonly List<Border> _stripScreens = new();
    private readonly List<TextBlock> _stripPlus = new();
    private readonly List<double> _stripLefts = new();
    private int _stripCount = -1;
    private double _stripDesignW = SingleCanvasW;

    private string _lastKey = "";
    private (int x, int y, int w, int h) _lastPlace = (-99999, 0, 0, 0);

    // Monitor-map mode: a plus of display squares (center + up/down/left/right).
    private Grid? _map;
    private Border? _mapUp, _mapDown, _mapLeft, _mapRight, _mapCenter;
    private Border? _mapUpFill, _mapDownFill, _mapLeftFill, _mapRightFill, _mapCenterFill;
    private double _mapDesignW = SingleCanvasW, _mapDesignH = CanvasH;

    // Down-action chooser: the snap HUD square stays visible but greyed out, and two circular
    // option buttons emerge downward beneath it — left = minimize (minus), right = close (red, X).
    // Leaning the swipe highlights one; swiping back up retracts and resumes normal gestures.
    private Grid? _chooser;
    private int _chooserCount = -1;
    private readonly List<System.Windows.Shapes.Ellipse> _chooserFills = new();       // accent fill (minimize only)
    private readonly List<System.Windows.Controls.TextBlock> _chooserIcons = new();
    private readonly List<Grid> _chooserCircles = new();                              // per-option container (for dim)
    private TranslateTransform? _chooserSlide;                                        // emerge-downward transform
    private Canvas? _chooserCircleHost;                                               // animated container for the circles
    private double _chooserDesignW = SingleCanvasW;
    private double _chooserDesignH = CanvasH;
    private bool _chooserActive;  // chooser currently engaged (drives the one-time slide reveal)
    private long _retractToken;    // guards the deferred hide after a retract animation

    // Circle option geometry (design space).
    private const double CircleD = 56;     // option-circle diameter
    private const double CircleGap = 22;   // gap between the two option circles
    private const double ChooserVGap = 18; // vertical gap between the greyed HUD square and the circles
    private static readonly Brush CloseFill = Freeze(new SolidColorBrush(Color.FromArgb(245, 0xE5, 0x48, 0x4A)));
    private static readonly Brush CircleBg = Freeze(new SolidColorBrush(Color.FromArgb(232, 30, 33, 40)));

    // The monitor map is anchored to the cursor position captured when the gesture
    // begins, so tiny pointer jitter while the fingers rest does not make the (large)
    // HUD twitch frame to frame. Reset when the HUD hides.
    private bool _mapAnchored;
    private int _mapAnchorX, _mapAnchorY;

    // Same idea for the desktop strip: capture the cursor once when the strip first appears
    // so the HUD holds still (and the reveal animation stays smooth) while the fingers rest,
    // instead of re-placing the window to follow micro-drift on every hold-update frame.
    private bool _stripAnchored;
    private int _stripAnchorX, _stripAnchorY;

    // Pending placement, re-applied after a WM_DPICHANGED. When the window crosses a
    // DPI boundary, WPF raises that message asynchronously and resizes the HWND to keep
    // its WPF Width/Height; we re-pin our exact pixel placement and only then reveal it,
    // so it never appears at the wrong (tiny/huge) size mid-transition.
    private double _pendDesignW = SingleCanvasW, _pendDesignH = CanvasH, _pendBasePx = BaseHeightPx;
    private int _pendCurX, _pendCurY;
    private bool _pendHaveCursor;
    private uint _pendDpi = 96;
    // Horizontal anchor: fraction of the design width that should sit under the
    // cursor (0.5 = centre). The desktop strip sets this to the current desktop's
    // slot so the active desktop stays under the cursor and the others fan out.
    private double _pendAnchorFrac = 0.5;

    /// <summary>Native handle of the HUD window once shown (else Zero). Used to carry
    /// the overlay across a virtual-desktop switch so it stays visible.</summary>
    public IntPtr Handle => _win == null ? IntPtr.Zero : new WindowInteropHelper(_win).Handle;

    /// <summary>Apply live appearance settings: whether the snap fill animates between
    /// zones, the highlight color (the Windows accent color or a custom hex), the HUD
    /// backdrop theme (dark, light, or follow the system light/dark setting), the HUD
    /// size, the HUD fade-out duration in seconds, and the snap-glide duration in ms
    /// (the fill glide matches the window-move speed).</summary>
    public void ApplyAppearance(bool animate, bool useAccent, string customHex, HudTheme mode, HudSize size, double fadeOutSeconds, double glideMs)
    {
        _animate = animate;
        _fadeOutMs = Math.Clamp(fadeOutSeconds, 0.1, 1.5) * 1000;
        _fillDuration = new Duration(TimeSpan.FromMilliseconds(Math.Clamp(glideMs, 50, 500)));

        _useAccent = useAccent;
        _customHex = customHex;
        ApplyHighlightColor(AccentColors.Resolve(useAccent, customHex));

        double scale = size == HudSize.Large ? 1.0 : 0.65;
        if (scale != _hudScale)
        {
            _hudScale = scale;
            // The Viewbox rescales automatically; force the next Place to reposition by
            // invalidating the cached placement.
            _lastPlace = (-99999, 0, 0, 0);
        }

        _hudMode = mode;
        ApplyEffectiveTheme();
    }

    /// <summary>Rebuild the solid/faint highlight brushes from a resolved color and recolor
    /// anything currently on screen so the change is visible immediately.</summary>
    private void ApplyHighlightColor(Color c)
    {
        _baseColor = c;
        _solid = Freeze(new SolidColorBrush(Color.FromArgb(235, c.R, c.G, c.B)));
        _faint = Freeze(new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B)));
        if (_singleFill is { Visibility: Visibility.Visible }) _singleFill.Background = _solid;
    }

    /// <summary>When the highlight follows the Windows accent color, re-read it (throttled)
    /// before showing so a changed accent appears without restarting. Skipped while a HUD is
    /// visible mid-gesture so the color never shifts under the user.</summary>
    private void SyncAccentColor()
    {
        if (!_useAccent || _shown) return;
        long now = Environment.TickCount64;
        if (now - _lastAccentCheckMs < 750) return;
        _lastAccentCheckMs = now;

        Color c = AccentColors.Resolve(true, _customHex);
        if (c != _baseColor) ApplyHighlightColor(c);
    }

    /// <summary>Resolve the backdrop theme to light or dark (reading the system setting when
    /// following it) and rebuild the HUD if it differs from what is currently built.</summary>
    private void ApplyEffectiveTheme()
    {
        bool light = _hudMode switch
        {
            HudTheme.Light => true,
            HudTheme.Dark => false,
            _ => SystemUsesLightTheme(),
        };
        if (light == _lightHud) return;

        _lightHud = light;
        _screenBg = light ? LightScreenBg : ScreenBg;
        _screenEdge = light ? LightEdge : WhiteEdge;
        // The backdrop is baked into each screen Border when built, so rebuild the whole
        // HUD visual tree to pick up the new theme. It is only shown during a gesture, so
        // tearing it down here just means the next gesture builds fresh.
        RebuildForTheme();
    }

    /// <summary>When following the system theme, re-resolve it (throttled) before showing,
    /// but never tear down a HUD that is currently visible mid-gesture.</summary>
    private void SyncSystemTheme()
    {
        if (_hudMode != HudTheme.System || _shown) return;
        long now = Environment.TickCount64;
        if (now - _lastThemeCheckMs < 750) return;
        _lastThemeCheckMs = now;
        ApplyEffectiveTheme();
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { /* default to dark on any failure */ }
        return false;
    }

    /// <summary>Discard the HUD window and all cached visual pieces so the next gesture
    /// rebuilds them with the current backdrop/bezel brushes.</summary>
    private void RebuildForTheme()
    {
        CancelHideTimer();
        _win?.Close();
        _win = null;
        _canvas = null;
        _single = null;
        _singleInner = null;
        _singleFill = null;
        _strip = null;
        _stripCount = -1;
        _stripFills.Clear();
        _stripScreens.Clear();
        _stripLefts.Clear();
        _map = null;
        _mapAnchored = false;
        _stripAnchored = false;
        _chooser = null;
        _chooserCount = -1;
        _chooserActive = false;
        _chooserFills.Clear();
        _chooserIcons.Clear();
        _chooserCircles.Clear();
        _shown = false;
        _lastKey = "";
        _lastPlace = (-99999, 0, 0, 0);
    }

    private void EnsureWindow()
    {
        if (_win != null) return;

        _canvas = new Canvas { Width = SingleCanvasW, Height = CanvasH };
        _single = BuildSingle();
        _canvas.Children.Add(_single);

        var box = new Viewbox { Stretch = Stretch.Fill, Child = _canvas };

        _win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            IsHitTestVisible = false,
            ShowActivated = false,
            Content = box,
            Width = 10,
            Height = 10,
            Left = -10000,
            Top = -10000,
        };
        _win.SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(_win!).Handle;
            long ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
            ex |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_LAYERED;
            Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(ex));
            HwndSource.FromHwnd(h)?.AddHook(WndProc);
        };
        // Keep the window permanently realized but fully transparent. We never call
        // Hide()/Show() again: toggling visibility is what leaks the cross-DPI rescale
        // as a visible twitch. Instead we only animate Opacity, so any WM_DPICHANGED
        // relayout happens on an already-shown, invisible window.
        _win.Opacity = 0;
        _win.Show();
    }

    private Grid BuildSingle()
    {
        var root = new Grid { Width = SingleCanvasW, Height = CanvasH };
        var (screen, inner, fill) = BuildScreen(SingleChipW);
        Canvas.SetLeft(screen, Margin);
        Canvas.SetTop(screen, Margin);
        // Grid ignores Canvas.Left/Top, so host the screen in a Canvas.
        var host = new Canvas { Width = SingleCanvasW, Height = CanvasH };
        host.Children.Add(screen);
        root.Children.Add(host);
        _singleInner = inner;
        _singleFill = fill;
        return root;
    }

    /// <summary>Builds (or rebuilds) the desktop strip for <paramref name="count"/>
    /// squares laid out left-to-right, each fill covering its whole inner area.</summary>
    private Grid BuildStrip(int count)
    {
        double chipsW = count * DeskW + (count - 1) * Gap;
        double canvasW = chipsW + 2 * Margin;
        var root = new Grid { Width = canvasW, Height = CanvasH };
        var host = new Canvas { Width = canvasW, Height = CanvasH };

        _stripFills.Clear();
        _stripScreens.Clear();
        _stripPlus.Clear();
        _stripLefts.Clear();
        for (int i = 0; i < count; i++)
        {
            var (screen, inner, fill) = BuildScreen(DeskW);
            double left = Margin + i * (DeskW + Gap);
            Canvas.SetLeft(screen, left);
            Canvas.SetTop(screen, Margin);
            host.Children.Add(screen);
            ResetFullFill(fill, DeskW);

            // A centred "+" glyph that marks a not-yet-created "new desktop" tile (used for the
            // overflow affordance). Sits on top of the fill, hidden until that tile is shown.
            double innerW = DeskW - 2 * Stroke;
            double innerH = ChipH - 2 * Stroke;
            var plus = new TextBlock
            {
                Text = "\uE710",   // Add (plus) symbol
                FontFamily = SymbolFont,
                FontSize = 22,
                Width = innerW,
                TextAlignment = TextAlignment.Center,
                Foreground = MutedIcon(),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(plus, 0);
            Canvas.SetTop(plus, (innerH - 26) / 2.0);
            inner.Children.Add(plus);

            _stripFills.Add(fill);
            _stripScreens.Add(screen);
            _stripPlus.Add(plus);
            _stripLefts.Add(left);
        }

        root.Children.Add(host);
        _stripDesignW = canvasW;
        return root;
    }

    private void EnsureStrip(int count)
    {
        if (_strip != null && _stripCount == count) return;
        if (_strip != null) _canvas!.Children.Remove(_strip);
        _strip = BuildStrip(count);
        _stripCount = count;
        _canvas!.Children.Add(_strip);
    }

    // ---- Down-action chooser (minimize / close) ----------------------------

    /// <summary>Build the chooser: <paramref name="count"/> chips (1 = close only, 2 = minimize
    /// then close), each a snap-style chip with a centered icon over a hidden fill.</summary>
    private Grid BuildChooser(int count)
    {
        // Content width is the wider of the greyed HUD square and the circle row.
        double circlesW = count * CircleD + (count - 1) * CircleGap;
        double contentW = Math.Max(SingleChipW, circlesW);
        double canvasW = contentW + 2 * Margin;
        double canvasH = Margin + ChipH + ChooserVGap + CircleD + Margin;

        var root = new Grid { Width = canvasW, Height = canvasH };
        var host = new Canvas { Width = canvasW, Height = canvasH };

        _chooserFills.Clear();
        _chooserIcons.Clear();
        _chooserCircles.Clear();

        // (1) The greyed-out snap HUD square on top, centred. It stays dimmed so the user knows
        // they can still swipe back up to resume normal snapping.
        var (screen, _, _) = BuildScreenWH(SingleChipW, ChipH);
        Canvas.SetLeft(screen, (canvasW - SingleChipW) / 2.0);
        Canvas.SetTop(screen, Margin);
        screen.Opacity = 0.32;
        host.Children.Add(screen);

        // (2) The option circles, emerging downward beneath the square.
        // For 2 options: minimize (left) then close (right). For 1: close only.
        var circleHost = new Canvas { Width = canvasW, Height = canvasH };
        _chooserCircleHost = circleHost;
        _chooserSlide = new TranslateTransform(0, 0);
        circleHost.RenderTransform = _chooserSlide;

        bool[] isClose = count == 1 ? new[] { true } : new[] { false, true };
        string[] glyphs = count == 1 ? new[] { "\uE8BB" } : new[] { "\uE921", "\uE8BB" };
        double rowLeft = (canvasW - circlesW) / 2.0;
        double rowTop = Margin + ChipH + ChooserVGap;

        for (int i = 0; i < count; i++)
        {
            double left = rowLeft + i * (CircleD + CircleGap);
            var (circle, accent, icon) = BuildOptionCircle(isClose[i], glyphs[i]);
            Canvas.SetLeft(circle, left);
            Canvas.SetTop(circle, rowTop);
            circleHost.Children.Add(circle);
            _chooserCircles.Add(circle);
            _chooserFills.Add(accent);
            _chooserIcons.Add(icon);
        }

        host.Children.Add(circleHost);
        root.Children.Add(host);
        _chooserDesignW = canvasW;
        _chooserDesignH = canvasH;
        return root;
    }

    /// <summary>One circular option button. Close circles are filled red with a white glyph;
    /// minimize circles use the dark HUD backdrop with a (hidden) accent fill revealed on select.
    /// Returns the container, the accent fill ellipse, and the glyph for later state changes.</summary>
    private (Grid circle, System.Windows.Shapes.Ellipse accent, System.Windows.Controls.TextBlock icon)
        BuildOptionCircle(bool isClose, string glyph)
    {
        var g = new Grid { Width = CircleD, Height = CircleD };

        var baseFill = new System.Windows.Shapes.Ellipse
        {
            Width = CircleD,
            Height = CircleD,
            Fill = isClose ? CloseFill : CircleBg,
            Stroke = isClose ? Freeze(new SolidColorBrush(Color.FromArgb(255, 0xFF, 0x6B, 0x6B))) : _screenEdge,
            StrokeThickness = Stroke,
        };
        g.Children.Add(baseFill);

        // Accent fill (minimize selection) sits above the dark backdrop, hidden until selected.
        var accent = new System.Windows.Shapes.Ellipse
        {
            Width = CircleD,
            Height = CircleD,
            Fill = _solid,
            Visibility = Visibility.Collapsed,
        };
        g.Children.Add(accent);

        var icon = new System.Windows.Controls.TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 24,
            Foreground = isClose ? Freeze(new SolidColorBrush(Colors.White)) : MutedIcon(),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        g.Children.Add(icon);

        return (g, accent, icon);
    }

    private Brush MutedIcon() => Freeze(new SolidColorBrush(_lightHud
        ? Color.FromArgb(190, 40, 44, 52)
        : Color.FromArgb(220, 255, 255, 255)));

    private void EnsureChooser(int count)
    {
        if (_chooser != null && _chooserCount == count) return;
        if (_chooser != null) _canvas!.Children.Remove(_chooser);
        _chooser = BuildChooser(count);
        _chooserCount = count;
        _canvas!.Children.Add(_chooser);
    }

    private void SetChooserMode()
    {
        if (_single == null || _chooser == null || _canvas == null) return;
        _single.Visibility = Visibility.Collapsed;
        if (_strip != null) _strip.Visibility = Visibility.Collapsed;
        if (_map != null) _map.Visibility = Visibility.Collapsed;
        _chooser.Visibility = Visibility.Visible;
        _canvas.Width = _chooserDesignW;
        _canvas.Height = _chooserDesignH;
    }

    /// <summary>Show the down-action chooser: a greyed snap-HUD square with circular minimize/close
    /// options emerging beneath it. Choose mode shows both circles and highlights the leaned-toward
    /// one; Close mode shows a single red close circle. Selection brightens the chosen circle (accent
    /// fill + white glyph for minimize, full-opacity red for close) and dims the other.</summary>
    public void ShowDownChooser(bool chooseMode, bool closePicked)
    {
        SyncSystemTheme();
        SyncAccentColor();
        EnsureWindow();
        if (_win == null) return;
        CancelHideTimer();

        int count = chooseMode ? 2 : 1;
        EnsureChooser(count);
        bool firstShow = !_chooserActive;
        SetChooserMode();
        _chooserActive = true;

        // Index of the selected circle and whether it is the (destructive) close action.
        int closeIndex = count == 2 ? 1 : 0;
        int selected = chooseMode ? (closePicked ? closeIndex : 0) : closeIndex;

        string key = $"dc|{count}|{selected}|{_baseColor}";
        if (key != _lastKey)
        {
            for (int i = 0; i < _chooserCircles.Count; i++)
            {
                bool active = i == selected;
                bool isClose = i == closeIndex;
                // Minimize: accent fill + white glyph when active; muted otherwise.
                if (!isClose)
                {
                    _chooserFills[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                    _chooserIcons[i].Foreground = active
                        ? Freeze(new SolidColorBrush(Colors.White))
                        : MutedIcon();
                }
                // Unselected circle dims back so the leaned-toward option clearly stands out.
                _chooserCircles[i].Opacity = active ? 1.0 : 0.5;
            }
            _lastKey = key;
        }
        Place(_chooserDesignW, _chooserDesignH, _chooserBasePx());

        // The circles "emerge" downward out of the greyed HUD square the first time the chooser
        // appears in a gesture; subsequent updates (lean changes) keep them steady.
        if (firstShow) RevealChooserDown();
    }

    // Scale the chooser window so the greyed square reads at the same physical size as a normal
    // single chip (the canvas is taller to fit the option circles).
    private double _chooserBasePx() => BaseHeightPx * _chooserDesignH / CanvasH;

    private void RevealChooserDown()
    {
        if (_chooserSlide == null) return;
        _retractToken++;  // invalidate any in-flight retract's deferred hide
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(-(CircleD + ChooserVGap) * 0.6, 0, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = ease,
        };
        _chooserSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        // Fade in as they slide so they don't read as overlapping the greyed square mid-emerge.
        _chooserCircleHost?.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))));
    }

    /// <summary>Cancel the chooser by retracting the circles back up into the greyed square, then
    /// hiding the HUD. Called when the user reverses the down swipe to escape the chooser.</summary>
    public void RetractChooserUp()
    {
        if (!_chooserActive || _chooserSlide == null || _win == null) { Hide(); return; }
        _chooserActive = false;
        _lastKey = "";
        long token = ++_retractToken;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var slide = new DoubleAnimation(0, -(CircleD + ChooserVGap) * 1.1, new Duration(TimeSpan.FromMilliseconds(360)))
        {
            EasingFunction = ease,
        };
        _chooserSlide.BeginAnimation(TranslateTransform.YProperty, slide);

        // Hold opacity briefly so the upward slide is clearly visible, then fade the circles out.
        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            BeginTime = TimeSpan.FromMilliseconds(100),
        };
        fade.Completed += (_, _) =>
        {
            // Only hide if this retract is still the current intent: a new chooser engage or a
            // normal snap gesture taking over (chooser collapsed) must not be hidden by us.
            if (token == _retractToken && _chooser != null && _chooser.Visibility == Visibility.Visible)
                Hide();
        };
        _chooserCircleHost?.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Builds one monitor: rounded screen with a clipped inner canvas and a
    /// (hidden) highlight fill rectangle. Backdrop and bezel follow the active HUD theme.
    /// Returns the pieces for later updates.</summary>
    private (Border screen, Canvas inner, Border fill) BuildScreen(double chipW) =>
        BuildScreenWH(chipW, ChipH);

    private (Border screen, Canvas inner, Border fill) BuildScreenWH(double chipW, double chipH)
    {
        double innerW = chipW - 2 * Stroke;
        double innerH = chipH - 2 * Stroke;
        double innerCorner = Math.Max(0, Corner - Stroke);

        var fill = new Border
        {
            Background = DefaultSolid,
            CornerRadius = new CornerRadius(0),
            Visibility = Visibility.Collapsed,
        };

        var inner = new Canvas
        {
            Width = innerW,
            Height = innerH,
            Clip = FreezeGeom(new RectangleGeometry(new Rect(0, 0, innerW, innerH), innerCorner, innerCorner)),
        };
        inner.Children.Add(fill);

        var screen = new Border
        {
            Width = chipW,
            Height = chipH,
            CornerRadius = new CornerRadius(Corner),
            BorderThickness = new Thickness(Stroke),
            BorderBrush = _screenEdge,
            Background = _screenBg,
            Child = inner,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 7,
                ShadowDepth = 1.5,
                Opacity = _lightHud ? 0.28 : 0.45,
                Direction = 270,
            },
        };
        return (screen, inner, fill);
    }

    private static Geometry FreezeGeom(Geometry g) { g.Freeze(); return g; }

    private static void ResetFullFill(Border fill, double chipW) => ResetFullFillWH(fill, chipW, ChipH);

    private static void ResetFullFillWH(Border fill, double chipW, double chipH)
    {
        double innerW = chipW - 2 * Stroke;
        double innerH = chipH - 2 * Stroke;
        fill.Width = innerW;
        fill.Height = innerH;
        Canvas.SetLeft(fill, 0);
        Canvas.SetTop(fill, 0);
    }

    // -------------------------------------------------------------------------

    /// <summary>Builds the move-to-display plus: five display squares (center plus
    /// up/down/left/right neighbors). Neighbor squares are shown only when a monitor
    /// actually exists in that direction.</summary>
    private Grid BuildMap()
    {
        double cw = 3 * MapCellW + 2 * MapGap;
        double ch = 3 * MapCellH + 2 * MapGap;
        var root = new Grid { Width = cw, Height = ch };
        var host = new Canvas { Width = cw, Height = ch };

        (Border screen, Border fill) Cell(int col, int row)
        {
            var (screen, _, fill) = BuildScreenWH(MapCellW, MapCellH);
            Canvas.SetLeft(screen, col * (MapCellW + MapGap));
            Canvas.SetTop(screen, row * (MapCellH + MapGap));
            host.Children.Add(screen);
            ResetFullFillWH(fill, MapCellW, MapCellH);
            return (screen, fill);
        }

        (_mapUp, _mapUpFill) = Cell(1, 0);
        (_mapLeft, _mapLeftFill) = Cell(0, 1);
        (_mapCenter, _mapCenterFill) = Cell(1, 1);
        (_mapRight, _mapRightFill) = Cell(2, 1);
        (_mapDown, _mapDownFill) = Cell(1, 2);

        root.Children.Add(host);
        _mapDesignW = cw;
        _mapDesignH = ch;
        return root;
    }

    private void EnsureMap()
    {
        if (_map != null) return;
        _map = BuildMap();
        _canvas!.Children.Add(_map);
    }

    private void SetMapMode()
    {
        if (_single == null || _map == null || _canvas == null) return;
        _single.Visibility = Visibility.Collapsed;
        if (_strip != null) _strip.Visibility = Visibility.Collapsed;
        if (_chooser != null) _chooser.Visibility = Visibility.Collapsed;
        _chooserActive = false;
        _map.Visibility = Visibility.Visible;
        _canvas.Width = _mapDesignW;
        _canvas.Height = _mapDesignH;
    }

    /// <summary>Show the monitor-map plus. Only directions with an actual neighbor
    /// monitor are drawn; the current display is faintly tinted and the swiped-at
    /// target (when a monitor exists there) fills solid.</summary>
    public void ShowMonitorMap(bool up, bool down, bool left, bool right, MonitorDirection? target)
    {
        SyncSystemTheme();
        SyncAccentColor();
        EnsureWindow();
        if (_win == null) return;
        CancelHideTimer();
        EnsureMap();
        SetMapMode();

        // All five squares stay visible; a direction with no monitor is dimmed so the
        // layout reads as "you can't go there" rather than vanishing.
        _mapUp!.Opacity = up ? 1.0 : MapDisabledOpacity;
        _mapDown!.Opacity = down ? 1.0 : MapDisabledOpacity;
        _mapLeft!.Opacity = left ? 1.0 : MapDisabledOpacity;
        _mapRight!.Opacity = right ? 1.0 : MapDisabledOpacity;

        string key = $"map|{up}{down}{left}{right}|{target}";
        if (key != _lastKey)
        {
            // Current monitor: solid "you are here" at rest, matching the desktop strip and
            // snap HUD. It dims to a faint tint only while you aim at a real neighbour (which
            // then takes the solid highlight), so the two never compete.
            bool aimingExisting = target switch
            {
                MonitorDirection.Up => up,
                MonitorDirection.Down => down,
                MonitorDirection.Left => left,
                MonitorDirection.Right => right,
                _ => false,
            };
            _mapCenterFill!.Background = aimingExisting ? _faint : _solid;
            _mapCenterFill.Visibility = Visibility.Visible;

            SetMapTarget(_mapUpFill!, up, target == MonitorDirection.Up);
            SetMapTarget(_mapDownFill!, down, target == MonitorDirection.Down);
            SetMapTarget(_mapLeftFill!, left, target == MonitorDirection.Left);
            SetMapTarget(_mapRightFill!, right, target == MonitorDirection.Right);
            _lastKey = key;
        }

        // Anchor to the cursor once per gesture so the HUD holds still while resting.
        if (!_mapAnchored && Win32.GetCursorPos(out var pt))
        {
            _mapAnchorX = pt.X;
            _mapAnchorY = pt.Y;
            _mapAnchored = true;
        }
        Place(_mapDesignW, _mapDesignH, MapBaseHeightPx, _mapAnchored ? (_mapAnchorX, _mapAnchorY) : null);
    }

    private void SetMapTarget(Border fill, bool exists, bool isTarget)
    {
        if (exists && isTarget)
        {
            fill.Background = _solid;
            fill.Visibility = Visibility.Visible;
        }
        else
        {
            fill.Visibility = Visibility.Collapsed;
        }
    }

    // -------------------------------------------------------------------------
    public void ShowSnap(SnapZone zone, double progress)
    {
        SyncSystemTheme();
        SyncAccentColor();
        EnsureWindow();
        if (_win == null) return;
        CancelHideTimer();
        SetSingleMode();

        string key = $"s|{zone}";
        if (key != _lastKey)
        {
            UpdateSingleFill(zone);
            _lastKey = key;
        }
        Place(SingleCanvasW, CanvasH);
    }

    private void UpdateSingleFill(SnapZone zone) => UpdateSingleFillFrac(ZoneFraction(zone), zone == SnapZone.Center);

    /// <summary>Show the chip highlighting an arbitrary fractional rect of the screen
    /// (used for the pinch-in restore preview, whose target isn't a fixed snap zone).</summary>
    public void ShowFraction(double x0, double y0, double x1, double y1, double progress)
    {
        EnsureWindow();
        if (_win == null) return;
        CancelHideTimer();
        SetSingleMode();

        string key = $"f|{x0:F3},{y0:F3},{x1:F3},{y1:F3}";
        if (key != _lastKey)
        {
            UpdateSingleFillFrac((x0, y0, x1, y1), rounded: true);
            _lastKey = key;
        }
        Place(SingleCanvasW, CanvasH);
    }

    private void UpdateSingleFillFrac((double, double, double, double)? frac, bool rounded)
    {
        if (_singleFill == null) return;
        if (frac == null)
        {
            ClearFillAnimations();
            _singleFill.Visibility = Visibility.Collapsed;
            return;
        }

        double innerW = SingleChipW - 2 * Stroke;
        double innerH = ChipH - 2 * Stroke;
        var (x0, y0, x1, y1) = frac.Value;

        double tw = Math.Max(0, (x1 - x0) * innerW);
        double th = Math.Max(0, (y1 - y0) * innerH);
        double tl = x0 * innerW;
        double tt = y0 * innerH;

        _singleFill.CornerRadius = new CornerRadius(rounded ? 4 : 0);
        _singleFill.Background = _solid;

        bool wasVisible = _singleFill.Visibility == Visibility.Visible;
        _singleFill.Visibility = Visibility.Visible;

        // Glide between zones only when already on screen; the first appearance snaps in.
        if (_animate && wasVisible)
        {
            AnimateTo(_singleFill, FrameworkElement.WidthProperty, tw);
            AnimateTo(_singleFill, FrameworkElement.HeightProperty, th);
            AnimateTo(_singleFill, Canvas.LeftProperty, tl);
            AnimateTo(_singleFill, Canvas.TopProperty, tt);
        }
        else
        {
            SetImmediate(_singleFill, FrameworkElement.WidthProperty, tw);
            SetImmediate(_singleFill, FrameworkElement.HeightProperty, th);
            SetImmediate(_singleFill, Canvas.LeftProperty, tl);
            SetImmediate(_singleFill, Canvas.TopProperty, tt);
        }
    }

    private void AnimateTo(UIElement el, DependencyProperty prop, double to)
    {
        var anim = new DoubleAnimation(to, _fillDuration) { EasingFunction = FillEase };
        el.BeginAnimation(prop, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetImmediate(UIElement el, DependencyProperty prop, double value)
    {
        el.BeginAnimation(prop, null); // drop any running animation so the value sticks
        el.SetValue(prop, value);
    }

    private void ClearFillAnimations()
    {
        if (_singleFill == null) return;
        _singleFill.BeginAnimation(FrameworkElement.WidthProperty, null);
        _singleFill.BeginAnimation(FrameworkElement.HeightProperty, null);
        _singleFill.BeginAnimation(Canvas.LeftProperty, null);
        _singleFill.BeginAnimation(Canvas.TopProperty, null);
    }

    /// <summary>Show the desktop mini-map: <paramref name="count"/> squares with the
    /// current desktop (where the held window lives) filled solid blue, and the neighbor
    /// you are leaning toward faintly tinted. Stays up until the gesture ends.</summary>
    public void ShowDesktopStrip(int count, int currentIndex, DesktopDirection? lean, bool animateReveal = false, bool previewDestination = false, int destIndexOverride = -1, bool overflowNewTile = false)
    {
        if (count < 1) count = 1;
        SyncSystemTheme();
        SyncAccentColor();
        EnsureWindow();
        if (_win == null) return;
        CancelHideTimer();
        EnsureStrip(count);
        SetStripMode();

        // The last tile is a not-yet-created "new desktop" when overflow is enabled: it carries a
        // "+" and reads as a ghost slot until the user aims onto it.
        int newTileIdx = overflowNewTile ? count - 1 : -1;

        // The highlighted neighbour/target: an explicit index (multi-desktop preview) when
        // provided, otherwise the single leaned-toward neighbour.
        int leanIdx = destIndexOverride >= 0
            ? destIndexOverride
            : lean switch
            {
                DesktopDirection.Right => currentIndex + 1,
                DesktopDirection.Left => currentIndex - 1,
                _ => -1,
            };

        // When previewing the destination, the desktop being aimed at (where the window
        // will land) gets the solid highlight and the current desktop is dimmed, so the HUD
        // reads "going here". Otherwise the current desktop stays solid and the leaned-toward
        // neighbour is faintly tinted. A target equal to the current desktop is not a move.
        bool emphasizeDest = previewDestination && leanIdx >= 0 && leanIdx < count && leanIdx != currentIndex;

        string key = $"strip|{count}|{currentIndex}|{leanIdx}|{(emphasizeDest ? 1 : 0)}|{newTileIdx}";
        if (key != _lastKey)
        {
            for (int i = 0; i < _stripFills.Count; i++)
            {
                var fill = _stripFills[i];
                bool isNewTile = i == newTileIdx;
                bool aimingNewTile = isNewTile && emphasizeDest && i == leanIdx;
                if (emphasizeDest && i == leanIdx)
                {
                    fill.Background = _solid;
                    fill.Visibility = Visibility.Visible;
                }
                else if (emphasizeDest && i == currentIndex)
                {
                    fill.Background = _faint;
                    fill.Visibility = Visibility.Visible;
                }
                else if (!emphasizeDest && i == currentIndex)
                {
                    fill.Background = _solid;
                    fill.Visibility = Visibility.Visible;
                }
                else if (!emphasizeDest && i == leanIdx && leanIdx >= 0 && leanIdx < count)
                {
                    fill.Background = _faint;
                    fill.Visibility = Visibility.Visible;
                }
                else fill.Visibility = Visibility.Collapsed;

                // Drive the "+" glyph and the ghost dimming on the new-desktop tile.
                if (i < _stripPlus.Count)
                {
                    var plus = _stripPlus[i];
                    plus.Visibility = isNewTile ? Visibility.Visible : Visibility.Collapsed;
                    if (isNewTile)
                    {
                        // Bright white "+" when the user is aiming onto it (about to create);
                        // muted otherwise so the slot reads as an available affordance.
                        plus.Foreground = aimingNewTile
                            ? Freeze(new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)))
                            : MutedIcon();
                    }
                }
                // The ghost slot sits dimmed until it is the active destination.
                if (isNewTile && i < _stripScreens.Count)
                    _stripScreens[i].Opacity = aimingNewTile ? 1.0 : 0.6;
                else if (i < _stripScreens.Count)
                    _stripScreens[i].Opacity = 1.0;
            }
            _lastKey = key;
        }

        // Prime the reveal start-state (collapse the other desktops onto the current one)
        // BEFORE placing/resizing the window. Otherwise the SetWindowPos resize inside Place
        // can trigger a paint that catches the strip fully expanded for a frame, so it
        // flashes the final layout, vanishes, then blooms.
        if (animateReveal) PrimeStripReveal(currentIndex);

        // Anchor the strip so the current desktop sits under the cursor (where the
        // dwell chip was), so the others appear to come out of the current monitor.
        double anchorFrac = 0.5;
        if (currentIndex >= 0 && currentIndex < _stripLefts.Count && _stripDesignW > 0)
            anchorFrac = (_stripLefts[currentIndex] + DeskW / 2.0) / _stripDesignW;
        // Anchor to the cursor once per gesture so the strip holds still while the fingers
        // rest, keeping the reveal smooth instead of jittering as the window chases cursor
        // micro-drift on every hold-update frame.
        if (!_stripAnchored && Win32.GetCursorPos(out var anchorPt))
        {
            _stripAnchorX = anchorPt.X;
            _stripAnchorY = anchorPt.Y;
            _stripAnchored = true;
        }
        Place(_stripDesignW, CanvasH, fixedCursor: _stripAnchored ? (_stripAnchorX, _stripAnchorY) : null, anchorFrac: anchorFrac);

        // Now that the window is placed and sized, start the bloom from the primed state.
        if (animateReveal) RunStripReveal(currentIndex);
    }

    /// <summary>Set the reveal start-state synchronously: the current desktop sits solid in
    /// its slot; every other square is collapsed onto the current one and made invisible.
    /// Called before the window is placed/resized so a resize-driven paint never catches the
    /// strip fully expanded. When animation is disabled, every square just sits in its slot.</summary>
    private void PrimeStripReveal(int currentIndex)
    {
        int n = _stripScreens.Count;
        if (n == 0) return;
        if (currentIndex < 0 || currentIndex >= n) currentIndex = 0;

        // Origin = the current desktop's own slot, so the others bloom out of the current
        // monitor (which sits under the cursor) rather than from the strip centre.
        double originLeft = _stripLefts[currentIndex];

        for (int i = 0; i < n; i++)
        {
            var screen = _stripScreens[i];
            double finalLeft = _stripLefts[i];
            bool isCurrent = i == currentIndex;

            // Clear any held animation from a previous reveal before re-pinning values.
            screen.BeginAnimation(Canvas.LeftProperty, null);
            screen.BeginAnimation(UIElement.OpacityProperty, null);

            if (!_animate || n == 1 || isCurrent)
            {
                // The current desktop is the anchor: solid, in its slot, no movement.
                Canvas.SetLeft(screen, finalLeft);
                screen.Opacity = 1;
            }
            else
            {
                Canvas.SetLeft(screen, originLeft);
                screen.Opacity = 0;
            }
        }
    }

    /// <summary>Start the reveal animations from the primed start-state: each non-current
    /// square slides out to its slot and fades in, cascading outward from the current desktop
    /// by distance. Assumes <see cref="PrimeStripReveal"/> already set the collapsed state.</summary>
    private void RunStripReveal(int currentIndex)
    {
        if (!_animate) return;
        int n = _stripScreens.Count;
        if (n <= 1) return;
        if (currentIndex < 0 || currentIndex >= n) currentIndex = 0;

        double originLeft = _stripLefts[currentIndex];

        for (int i = 0; i < n; i++)
        {
            if (i == currentIndex) continue;
            var screen = _stripScreens[i];
            double finalLeft = _stripLefts[i];

            // Squares further from the current one start a touch later and travel a touch
            // longer, so the strip unfolds outward from the current desktop.
            int distance = Math.Abs(i - currentIndex);
            var begin = TimeSpan.FromMilliseconds(34 * (distance - 1));

            var slide = new DoubleAnimation(originLeft, finalLeft, RevealDuration)
            {
                BeginTime = begin,
                EasingFunction = RevealEase,
            };
            screen.BeginAnimation(Canvas.LeftProperty, slide);

            var fade = new DoubleAnimation(0, 1, RevealFadeDuration)
            {
                BeginTime = begin,
                EasingFunction = RevealEase,
            };
            screen.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    private void SetSingleMode()
    {
        if (_single == null || _canvas == null) return;
        _single.Visibility = Visibility.Visible;
        if (_strip != null) _strip.Visibility = Visibility.Collapsed;
        if (_map != null) _map.Visibility = Visibility.Collapsed;
        if (_chooser != null) _chooser.Visibility = Visibility.Collapsed;
        _chooserActive = false;
        _canvas.Width = SingleCanvasW;
        _canvas.Height = CanvasH;
    }

    private void SetStripMode()
    {
        if (_single == null || _strip == null || _canvas == null) return;
        _single.Visibility = Visibility.Collapsed;
        _strip.Visibility = Visibility.Visible;
        if (_map != null) _map.Visibility = Visibility.Collapsed;
        if (_chooser != null) _chooser.Visibility = Visibility.Collapsed;
        _chooserActive = false;
        _canvas.Width = _stripDesignW;
        _canvas.Height = CanvasH;
    }

    /// <summary>Map a snap zone to a fractional rect [x0,y0,x1,y1] within the screen area.</summary>
    private static (double, double, double, double)? ZoneFraction(SnapZone zone) => zone switch
    {
        SnapZone.LeftHalf => (0, 0, 0.5, 1),
        SnapZone.RightHalf => (0.5, 0, 1, 1),
        SnapZone.TopHalf => (0, 0, 1, 0.5),
        SnapZone.BottomHalf => (0, 0.5, 1, 1),
        SnapZone.TopLeft => (0, 0, 0.5, 0.5),
        SnapZone.TopRight => (0.5, 0, 1, 0.5),
        SnapZone.BottomLeft => (0, 0.5, 0.5, 1),
        SnapZone.BottomRight => (0.5, 0.5, 1, 1),
        SnapZone.Maximize => (0, 0, 1, 1),
        SnapZone.Center => (0.2, 0.2, 0.8, 0.8),
        SnapZone.LeftThird => (0, 0, 1.0 / 3, 1),
        SnapZone.CenterThird => (1.0 / 3, 0, 2.0 / 3, 1),
        SnapZone.RightThird => (2.0 / 3, 0, 1, 1),
        SnapZone.LeftTwoThird => (0, 0, 2.0 / 3, 1),
        SnapZone.RightTwoThird => (1.0 / 3, 0, 1, 1),
        SnapZone.TopThird => (0, 0, 1, 1.0 / 3),
        SnapZone.CenterRowThird => (0, 1.0 / 3, 1, 2.0 / 3),
        SnapZone.BottomThird => (0, 2.0 / 3, 1, 1),
        SnapZone.TopTwoThird => (0, 0, 1, 2.0 / 3),
        SnapZone.BottomTwoThird => (0, 1.0 / 3, 1, 1),
        SnapZone.ThirdTopLeft => (0, 0, 1.0 / 3, 1.0 / 3),
        SnapZone.ThirdTopRight => (2.0 / 3, 0, 1, 1.0 / 3),
        SnapZone.ThirdBottomLeft => (0, 2.0 / 3, 1.0 / 3, 1),
        SnapZone.ThirdBottomRight => (2.0 / 3, 2.0 / 3, 1, 1),
        SnapZone.Minimize => (0.32, 0.82, 0.68, 1.0),
        _ => null,
    };

    private void Place(double designW, double designH, double basePx = BaseHeightPx, (int x, int y)? fixedCursor = null, double anchorFrac = 0.5)
    {
        if (_win == null) return;

        _pendDesignW = designW;
        _pendDesignH = designH;
        _pendBasePx = basePx;
        _pendAnchorFrac = anchorFrac;
        if (fixedCursor is { } fc) { _pendCurX = fc.x; _pendCurY = fc.y; _pendHaveCursor = true; }
        else if (Win32.GetCursorPos(out var pt)) { _pendCurX = pt.X; _pendCurY = pt.Y; _pendHaveCursor = true; }
        else _pendHaveCursor = false;

        ApplyPlacement();

        // Reveal only once the window's WPF scale matches the target monitor. If the move
        // crossed a DPI boundary, WPF raises WM_DPICHANGED asynchronously and the hook
        // re-pins and reveals then. If no boundary was crossed, this reveals immediately.
        if (!_shown)
            _win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryReveal));
    }

    /// <summary>Size and position the window in physical pixels for the pending cursor and
    /// that monitor's DPI, and pin a matching WPF Width/Height (in DIPs) so WPF's own
    /// per-monitor auto-resize converges on the same size instead of snapping the window
    /// back to its stale design size after a cross-DPI move.</summary>
    private void ApplyPlacement()
    {
        if (_win == null) return;

        uint dpi = Win32.GetDpiForCursor();
        _pendDpi = dpi;
        double s = dpi / 96.0;
        int physH = (int)Math.Round(_pendBasePx * s * _hudScale);
        int physW = (int)Math.Round(physH * _pendDesignW / _pendDesignH);

        // DIP size is DPI-independent: this is the size WPF must preserve across monitors.
        _win.Width = physW / s;
        _win.Height = physH / s;

        int x = -10000, y = -10000;
        if (_pendHaveCursor)
        {
            x = _pendCurX - (int)Math.Round(_pendAnchorFrac * physW);
            y = _pendCurY + (int)Math.Round(14 * s);
        }

        var place = (x, y, physW, physH);
        if (place != _lastPlace)
        {
            _lastPlace = place;
            IntPtr h = new WindowInteropHelper(_win).Handle;
            Win32.SetWindowPos(h, Win32.HWND_TOPMOST, x, y, physW, physH, Win32.SWP_NOACTIVATE);
        }
    }

    /// <summary>Reveal the HUD only when WPF's reported scale matches the cursor monitor's
    /// DPI, so the window is never shown mid-DPI-transition at the wrong size.</summary>
    private void TryReveal()
    {
        if (_win == null || _shown) return;
        uint winDpi = (uint)Math.Round(VisualTreeHelper.GetDpi(_win).PixelsPerDip * 96);
        if (winDpi == _pendDpi)
        {
            // Interrupt any in-flight fade-out and snap to fully visible.
            _fadeToken++;
            _win.BeginAnimation(UIElement.OpacityProperty, null);
            _win.Opacity = 1;
            _shown = true;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_DPICHANGED && _win != null)
        {
            // WPF updates its composition scale in its own handler for this message. After
            // that settles, re-pin our exact pixel placement (WPF may have nudged the rect)
            // and reveal. Render priority runs after WPF's relayout for this DPI change.
            _win.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _lastPlace = (-99999, 0, 0, 0); // force re-apply over WPF's auto-resize
                ApplyPlacement();
                TryReveal();
            }));
        }
        return IntPtr.Zero;
    }

    public void Hide()
    {
        CancelHideTimer();
        _lastKey = "";
        _lastPlace = (-99999, 0, 0, 0);
        _mapAnchored = false;
        _stripAnchored = false;
        _chooserActive = false;
        _shown = false;
        if (_win == null) return;

        int token = ++_fadeToken;

        // With animations off, drop instantly. Otherwise fade the whole HUD out, then
        // reset the snap fill so the next gesture's first zone appears fresh.
        if (!_animate)
        {
            _win.BeginAnimation(UIElement.OpacityProperty, null);
            _win.Opacity = 0;
            ClearFillAnimations();
            if (_singleFill != null) _singleFill.Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(_fadeOutMs)))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        fade.Completed += (_, _) =>
        {
            // Skip if a newer reveal or hide superseded this fade.
            if (token != _fadeToken || _win == null) return;
            _win.BeginAnimation(UIElement.OpacityProperty, null);
            _win.Opacity = 0;
            ClearFillAnimations();
            if (_singleFill != null) _singleFill.Visibility = Visibility.Collapsed;
        };
        _win.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void CancelHideTimer()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
    }

    public void Close()
    {
        CancelHideTimer();
        _win?.Close();
        _win = null;
        _canvas = null;
    }
}
