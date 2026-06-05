using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Swoosh.Settings;
using Swoosh.Updates;
using MUXC = Microsoft.UI.Xaml.Controls;

namespace Swoosh.SettingsApp;

/// <summary>
/// WinUI 3 settings window. Mirrors the old WPF settings surface (general toggles,
/// thirds modifier, sensitivity, overlay color, update check + changelog) but writes
/// to the same settings.json, so changes apply live to the running tray app.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly UpdateChecker _updates = new();
    private readonly SwooshStats _stats = new();
    private bool _loading;
    private string? _downloadUrl;
    private string _overlayColor = "#0A84FF";

    private static readonly string[] SwatchColors =
        { "#0A84FF", "#5AC8FA", "#34C759", "#AF52DE", "#FF2D55", "#FF9500", "#FFD60A", "#8E8E93" };
    private readonly List<Button> _swatches = new();

    // ---- Per-gesture enable tiles (Swish-style) ----------------------------
    private sealed record GestureDef(string Key, string Name, string Gesture,
        double X0, double Y0, double X1, double Y1, bool Grid = false);

    private static readonly GestureDef[] Gestures =
    {
        new("maximize", "Maximize",    "Swipe up",          0.00, 0.00, 1.00, 1.00),
        new("halves",   "Halves",      "Swipe left/right",  0.00, 0.00, 0.50, 1.00),
        new("quarters", "Quarters",    "Swipe diagonally",  0.00, 0.00, 0.50, 0.50),
        new("minimize", "Minimize",    "Swipe down",        0.28, 0.74, 0.72, 0.94),
        new("center",   "Center",      "Five-finger tap",   0.24, 0.28, 0.76, 0.72),
        new("thirds",   "Thirds grid", "Modifier + swipe",  0.00, 0.00, 0.00, 0.00, Grid: true),
    };

    private readonly Dictionary<string, bool> _gestureEnabled = new();
    private readonly List<(string Key, Button Card)> _gestureCards = new();

    // Code-created TextBlocks that should use the theme-aware "secondary" text
    // colour. We track them so they recolour on theme change — pulling the brush
    // from Application.Current.Resources returns a light-theme snapshot that
    // renders near-black in dark mode.
    private readonly List<TextBlock> _secondaryTexts = new();

    public MainWindow()
    {
        InitializeComponent();

        Title = "Swoosh Settings";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ResizeForDpi(860, 680);
        TrySetWindowIcon();

        UpdateCaptionButtonColors();
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            UpdateCaptionButtonColors();
            HighlightSwatch(_overlayColor);
            RefreshSecondaryTexts();
        };

        VersionText.Text = $"v{_updates.CurrentVersion}";
        BuildSwatches();
        BuildGestureCards();
        LoadFrom(_store.Current);

        _store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            _store.Changed -= OnStoreChanged;
            _stats.Dispose();
        };

        UpdateSwooshCount(_stats.LifetimeSwooshes);
        _stats.Changed += n => DispatcherQueue.TryEnqueue(() => UpdateSwooshCount(n));
        _stats.StartWatching();

        RootGrid.Loaded += async (_, _) =>
        {
            // Re-apply once the visual tree is loaded: in the constructor
            // RootGrid.ActualTheme hasn't resolved to the real (system) theme yet,
            // so the caption glyphs could be painted for the wrong theme and stay
            // that way if no ActualThemeChanged fires.
            UpdateCaptionButtonColors();
            HighlightSwatch(_overlayColor);
            RefreshSecondaryTexts();

            await RunUpdateCheck();
        };
    }

    /// <summary>Render the lifetime swoosh tally in the nav pane footer, with a
    /// thousands separator and singular/plural label.</summary>
    private void UpdateSwooshCount(long n)
    {
        SwooshCountText.Text = n.ToString("N0");
        SwooshCountLabel.Text = n == 1 ? "lifetime swoosh" : "lifetime swooshes";
    }

    /// <summary>Theme-aware "secondary" text colour for code-created TextBlocks.
    /// Pulling TextFillColorSecondaryBrush from Application.Current.Resources
    /// returns a fixed light-theme brush (dark text), which is unreadable in dark
    /// mode, so we resolve the WinUI default per the current actual theme.</summary>
    private SolidColorBrush SecondaryTextBrush()
    {
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        return new SolidColorBrush(dark
            ? Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x9E, 0x00, 0x00, 0x00));
    }

    /// <summary>Register a TextBlock as using the secondary colour and paint it now.
    /// Tracked blocks are recoloured on theme change.</summary>
    private TextBlock TrackSecondary(TextBlock tb)
    {
        tb.Foreground = SecondaryTextBrush();
        _secondaryTexts.Add(tb);
        return tb;
    }

    private void RefreshSecondaryTexts()
    {
        var brush = SecondaryTextBrush();
        foreach (var tb in _secondaryTexts)
            tb.Foreground = brush;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Paint the system caption buttons (minimize/maximize/close glyphs) to match
    /// the current theme. With an extended title bar + Mica these don't reliably follow the
    /// app theme on their own, leaving black glyphs in dark mode and white glyphs in light
    /// mode. We set the glyph and hover colours explicitly and keep the backgrounds
    /// transparent so Mica shows through.</summary>
    private void UpdateCaptionButtonColors()
    {
        var tb = AppWindow.TitleBar;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;

        Color fg = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0);
        Color disabled = dark ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(120, 0, 0, 0);
        Color hoverBg = dark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(25, 0, 0, 0);
        Color pressedBg = dark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0);

        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = disabled;

        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonHoverBackgroundColor = hoverBg;
        tb.ButtonPressedBackgroundColor = pressedBg;
    }

    /// <summary>Resize to a logical (DPI-independent) size. AppWindow.Resize takes
    /// physical pixels, so on a 150%/200% display a raw 800x640 would render tiny.</summary>
    private void ResizeForDpi(int logicalWidth, int logicalHeight)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(logicalWidth * scale),
            (int)Math.Round(logicalHeight * scale)));
    }

    /// <summary>Set the window's title-bar and taskbar icon from the swoosh.ico copied
    /// next to the executable. Best-effort: a missing file just leaves the default.</summary>
    private void TrySetWindowIcon()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "swoosh.ico");
            if (System.IO.File.Exists(path))
                AppWindow.SetIcon(path);
        }
        catch { /* non-fatal */ }
    }

    // ---- Load / collect ----------------------------------------------------

    private void LoadFrom(AppSettings s)
    {
        _loading = true;
        GesturesToggle.IsOn = s.GesturesEnabled;
        LaunchToggle.IsOn = s.LaunchAtLogin;
        _gestureEnabled["maximize"] = s.MaximizeEnabled;
        _gestureEnabled["halves"] = s.HalvesEnabled;
        _gestureEnabled["quarters"] = s.QuartersEnabled;
        _gestureEnabled["minimize"] = s.MinimizeEnabled;
        _gestureEnabled["center"] = s.CenterEnabled;
        _gestureEnabled["thirds"] = s.GridModifierEnabled;
        RefreshGestureCards();
        AnimateToggle.IsOn = s.AnimateSnaps;
        DebugToggle.IsOn = s.DebugOverlay;
        ModifierCombo.SelectedIndex = s.GridModifier switch
        {
            GridModifier.Ctrl => 1,
            GridModifier.Alt => 2,
            _ => 0,
        };
        ModifierCombo.IsEnabled = s.GridModifierEnabled;
        SensitivitySlider.Value = s.Sensitivity;        MonitorMoveToggle.IsOn = s.MonitorMoveEnabled;
        MonitorModifierCombo.SelectedIndex = s.MonitorMoveModifier switch
        {
            GridModifier.Ctrl => 1,
            GridModifier.Alt => 2,
            _ => 0,
        };
        MonitorModifierCombo.IsEnabled = s.MonitorMoveEnabled;
        OverlayAccentToggle.IsOn = s.OverlayUseAccent;
        _overlayColor = s.OverlayColor;
        HighlightSwatch(_overlayColor);
        SetSwatchesEnabled(!s.OverlayUseAccent);
        GridSpacingSlider.Value = Math.Clamp(s.GridSpacing, 0, 10);
        UpdateGridSpacingLabel(GridSpacingSlider.Value);
        CancelTimeoutSlider.Value = Math.Clamp(s.CancelTimeoutSeconds, 0, 3);
        UpdateCancelTimeoutLabel(CancelTimeoutSlider.Value);
        LivePreviewToggle.IsOn = s.LivePreview;
        MoveCursorToggle.IsOn = s.MoveCursor;
        PreviewDesktopDestinationToggle.IsOn = s.PreviewDesktopDestination;
        _loading = false;
    }

    private AppSettings Collect() => new()
    {
        GesturesEnabled = GesturesToggle.IsOn,
        LaunchAtLogin = LaunchToggle.IsOn,
        MaximizeEnabled = GestureOn("maximize"),
        HalvesEnabled = GestureOn("halves"),
        QuartersEnabled = GestureOn("quarters"),
        MinimizeEnabled = GestureOn("minimize"),
        CenterEnabled = GestureOn("center"),
        AnimateSnaps = AnimateToggle.IsOn,
        DebugOverlay = DebugToggle.IsOn,
        GridModifierEnabled = GestureOn("thirds"),
        GridModifier = ModifierCombo.SelectedIndex switch
        {
            1 => GridModifier.Ctrl,
            2 => GridModifier.Alt,
            _ => GridModifier.Shift,
        },
        Sensitivity = SensitivitySlider.Value,
        MonitorMoveEnabled = MonitorMoveToggle.IsOn,
        MonitorMoveModifier = MonitorModifierCombo.SelectedIndex switch
        {
            1 => GridModifier.Ctrl,
            2 => GridModifier.Alt,
            _ => GridModifier.Shift,
        },
        OverlayUseAccent = OverlayAccentToggle.IsOn,
        OverlayColor = _overlayColor,
        GridSpacing = (int)Math.Round(GridSpacingSlider.Value),
        CancelTimeoutSeconds = CancelTimeoutSlider.Value,
        LivePreview = LivePreviewToggle.IsOn,
        MoveCursor = MoveCursorToggle.IsOn,
        PreviewDesktopDestination = PreviewDesktopDestinationToggle.IsOn,
    };

    private void SaveIfReady()
    {
        if (_loading) return;
        _store.Save(Collect());
    }

    // ---- Control events ----------------------------------------------------

    private void OnSettingToggled(object sender, RoutedEventArgs e) => SaveIfReady();

    private void OnModifierChanged(object sender, SelectionChangedEventArgs e) => SaveIfReady();

    private void OnMonitorMoveToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        MonitorModifierCombo.IsEnabled = MonitorMoveToggle.IsOn;
        SaveIfReady();
    }

    private void OnMonitorModifierChanged(object sender, SelectionChangedEventArgs e) => SaveIfReady();

    private void OnSensitivityChanged(object sender, RangeBaseValueChangedEventArgs e) => SaveIfReady();

    private void OnGridSpacingChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateGridSpacingLabel(e.NewValue);
        SaveIfReady();
    }

    private void OnCancelTimeoutChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateCancelTimeoutLabel(e.NewValue);
        SaveIfReady();
    }

    private void UpdateGridSpacingLabel(double v)
    {
        if (GridSpacingValue != null) GridSpacingValue.Text = $"{(int)Math.Round(v)} px";
    }

    private void UpdateCancelTimeoutLabel(double v)
    {
        if (CancelTimeoutValue != null)
            CancelTimeoutValue.Text = v <= 0 ? "Off" : $"{v:0.0} s";
    }

    private void OnAccentToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetSwatchesEnabled(!OverlayAccentToggle.IsOn);
        SaveIfReady();
    }

    private void OnStoreChanged(AppSettings s)
    {
        // External change (the tray app or another instance saved): mirror it here.
        DispatcherQueue.TryEnqueue(() => LoadFrom(s));
    }

    // ---- Swatches ----------------------------------------------------------

    private void BuildSwatches()
    {
        foreach (var hex in SwatchColors)
        {
            var color = ParseColor(hex);
            var btn = new Button
            {
                Width = 32,
                Height = 32,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(2.5),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Background = new SolidColorBrush(color),
                Tag = hex,
            };
            // The default Button style swaps Background to a grey theme brush on
            // PointerOver/Pressed. Override those per-state brushes so each swatch keeps
            // its own colour while hovered or pressed instead of turning grey.
            btn.Resources["ButtonBackground"] = new SolidColorBrush(color);
            btn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(color);
            btn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(color);
            btn.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(color);
            ToolTipService.SetToolTip(btn, hex);
            btn.Click += (_, _) => OnSwatchPicked(hex);
            _swatches.Add(btn);
            SwatchPanel.Children.Add(btn);
        }
    }

    private void HighlightSwatch(string hex)
    {
        // The selection ring must contrast with the window background, not just the swatch:
        // a white ring vanishes against the light-mode backdrop. Use white on dark themes
        // and a near-black ring on light themes.
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var selBrush = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 0, 0, 0));
        foreach (var sw in _swatches)
        {
            bool sel = string.Equals((string)sw.Tag, hex, StringComparison.OrdinalIgnoreCase);
            sw.BorderBrush = sel ? selBrush : UnselectedSwatchStroke();
        }
    }

    /// <summary>A subtle theme-adaptive outline so every swatch is delineated from the
    /// window background — without it, darker colours blend into the dark-mode backdrop.
    /// The selected swatch overrides this with a solid white ring.</summary>
    private Brush UnselectedSwatchStroke()
    {
        if (Application.Current.Resources.TryGetValue("ControlStrokeColorSecondaryBrush", out var res)
            && res is Brush b)
            return b;
        // Fallback: a faint light stroke that still reads on a dark background.
        return new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
    }

    private void SetSwatchesEnabled(bool enabled)
    {
        SwatchPanel.IsHitTestVisible = enabled;
        SwatchPanel.Opacity = enabled ? 1.0 : 0.4;
    }

    private void OnSwatchPicked(string hex)
    {
        _overlayColor = hex;
        HighlightSwatch(hex);
        if (_loading) return;
        // Picking a custom color implies the accent mode should be off.
        OverlayAccentToggle.IsOn = false;
        _store.Save(Collect());
    }

    private static Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return Color.FromArgb(255, r, g, b);
    }

    // ---- Gesture tiles -----------------------------------------------------

    private bool GestureOn(string key) => !_gestureEnabled.TryGetValue(key, out var v) || v;

    /// <summary>Build the gesture tiles into a responsive, centered wrapping grid. Each tile
    /// shows a small window-shape HUD of where the gesture snaps, its name, and the gesture
    /// itself; clicking toggles it on/off (a disabled tile greys out, Swish-style). The grid
    /// reflows to fewer columns as the window narrows so no tile is ever clipped off-screen.</summary>
    private void BuildGestureCards()
    {
        GestureHost.Children.Clear();
        _gestureCards.Clear();

        var cards = new List<Button>();
        foreach (var g in Gestures)
        {
            _gestureEnabled[g.Key] = true;

            var content = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(BuildHud(g));
            content.Children.Add(new TextBlock
            {
                Text = g.Name,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            });
            content.Children.Add(TrackSecondary(new TextBlock
            {
                Text = g.Gesture,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            }));

            var card = new Button
            {
                Width = 150,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                Content = content,
                Tag = g.Key,
            };
            card.Click += OnGestureCardClicked;
            cards.Add(card);
            _gestureCards.Add((g.Key, card));
        }

        var repeater = new ItemsRepeater
        {
            ItemsSource = cards,
            Layout = new UniformGridLayout
            {
                MinItemWidth = 150,
                MinItemHeight = 118,
                MinColumnSpacing = 10,
                MinRowSpacing = 10,
                ItemsJustification = UniformGridLayoutItemsJustification.Center,
            },
        };
        GestureHost.Children.Add(repeater);
    }

    /// <summary>A window-shape HUD: a rounded "screen" frame with a uniform bezel and the
    /// snap zone filled in the accent color. The thirds tile draws a 3x3 cell grid instead.
    /// The fill sits inside the bezel so halves/quarters read exactly and rounded corners
    /// never let the background peek around a sharp fill.</summary>
    private static FrameworkElement BuildHud(GestureDef g)
    {
        const double innerW = 58, innerH = 36, bezel = 3;
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

        Grid inner = new()
        {
            Width = innerW,
            Height = innerH,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (g.Grid)
        {
            for (int c = 0; c < 3; c++) inner.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < 3; r++) inner.RowDefinitions.Add(new RowDefinition());
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    var cell = new Border
                    {
                        Margin = new Thickness(1),
                        CornerRadius = new CornerRadius(1),
                        Background = accent,
                    };
                    Grid.SetColumn(cell, c);
                    Grid.SetRow(cell, r);
                    inner.Children.Add(cell);
                }
        }
        else
        {
            inner.Children.Add(new Border
            {
                Width = Math.Max(2, (g.X1 - g.X0) * innerW),
                Height = Math.Max(2, (g.Y1 - g.Y0) * innerH),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(g.X0 * innerW, g.Y0 * innerH, 0, 0),
                CornerRadius = new CornerRadius(2),
                Background = accent,
            });
        }

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(bezel),
            BorderThickness = new Thickness(1.5),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(6),
            Child = inner,
        };
    }

    private void OnGestureCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        _gestureEnabled[key] = !GestureOn(key);
        ApplyGestureVisual(key);
        if (key == "thirds") ModifierCombo.IsEnabled = GestureOn("thirds");
        SaveIfReady();
    }

    private void RefreshGestureCards()
    {
        foreach (var (key, _) in _gestureCards) ApplyGestureVisual(key);
    }

    private void ApplyGestureVisual(string key)
    {
        foreach (var (k, card) in _gestureCards)
        {
            if (k != key) continue;
            bool on = GestureOn(key);
            if (card.Content is UIElement el) el.Opacity = on ? 1.0 : 0.35;
            return;
        }
    }

    // ---- Navigation --------------------------------------------------------

    private void Nav_SelectionChanged(MUXC.NavigationView sender, MUXC.NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as MUXC.NavigationViewItem)?.Tag as string ?? "general";
        if (GeneralPane == null) return; // not yet loaded

        GeneralPane.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        SnappingPane.Visibility = tag == "snapping" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePane.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPane.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        AboutPane.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Updates -----------------------------------------------------------

    private async void CheckBtn_Click(object sender, RoutedEventArgs e) => await RunUpdateCheck();

    private void DownloadBtn_Click(object sender, RoutedEventArgs e) => OpenUrl(_downloadUrl);

    private async Task RunUpdateCheck()
    {
        CheckBtn.IsEnabled = false;
        DownloadBtn.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "Checking for updates...";
        UpdateSub.Text = "";

        var info = await _updates.CheckAsync();
        if (info != null)
        {
            _downloadUrl = info.HtmlUrl;
            UpdateStatus.Text = $"Update available: v{info.Latest}";
            UpdateSub.Text = $"You're on v{_updates.CurrentVersion}.";
            DownloadBtn.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateStatus.Text = "You're up to date";
            UpdateSub.Text = $"v{_updates.CurrentVersion} is the latest release.";
        }
        CheckBtn.IsEnabled = true;
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser available */ }
    }
}
