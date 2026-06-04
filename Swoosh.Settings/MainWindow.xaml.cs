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

    public MainWindow()
    {
        InitializeComponent();

        Title = "Swoosh Settings";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ResizeForDpi(860, 680);

        VersionText.Text = $"v{_updates.CurrentVersion}";
        BuildSwatches();
        BuildGestureCards();
        LoadFrom(_store.Current);

        _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;

        RootGrid.Loaded += async (_, _) =>
        {
            await RunUpdateCheck();
            await LoadChangelog();
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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

    // ---- Load / collect ----------------------------------------------------

    private void LoadFrom(AppSettings s)
    {
        _loading = true;
        GesturesToggle.IsOn = s.GesturesEnabled;
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
        _loading = false;
    }

    private AppSettings Collect() => new()
    {
        GesturesEnabled = GesturesToggle.IsOn,
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
                Background = new SolidColorBrush(ParseColor(hex)),
                Tag = hex,
            };
            ToolTipService.SetToolTip(btn, hex);
            btn.Click += (_, _) => OnSwatchPicked(hex);
            _swatches.Add(btn);
            SwatchPanel.Children.Add(btn);
        }
    }

    private void HighlightSwatch(string hex)
    {
        foreach (var sw in _swatches)
        {
            bool sel = string.Equals((string)sw.Tag, hex, StringComparison.OrdinalIgnoreCase);
            sw.BorderBrush = new SolidColorBrush(sel ? Colors.White : Colors.Transparent);
        }
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
            content.Children.Add(new TextBlock
            {
                Text = g.Gesture,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            });

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

    private async Task LoadChangelog()
    {
        // Prefer the hand-written changelog bundled with the app so users see
        // curated notes; fall back to GitHub release notes if it's missing.
        if (TryRenderLocalChangelog()) return;

        var releases = await _updates.ReleasesAsync(1);
        ChangelogPanel.Children.Clear();

        if (releases.Count == 0)
        {
            ChangelogPanel.Children.Add(new TextBlock
            {
                Text = "Couldn't load release notes (offline or rate-limited).",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            return;
        }

        var r = releases[0];

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = r.Name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (r.Published is { } p)
        {
            var date = new TextBlock
            {
                Text = p.LocalDateTime.ToString("MMM d, yyyy"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            Grid.SetColumn(date, 1);
            header.Children.Add(date);
        }
        ChangelogPanel.Children.Add(header);

        var body = CleanBody(r.Body);
        if (!string.IsNullOrWhiteSpace(body))
        {
            ChangelogPanel.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
    }

    /// <summary>Reads the curated CHANGELOG.md. Prefers the copy embedded in the
    /// assembly (always present regardless of build config or launch path) and falls
    /// back to a copy next to the exe. Returns null if neither is available.</summary>
    private static string? ReadBundledChangelog()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("CHANGELOG.md");
            if (stream != null)
            {
                using var reader = new System.IO.StreamReader(stream);
                string embedded = reader.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(embedded)) return embedded;
            }
        }
        catch { /* fall through to disk */ }

        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
            if (System.IO.File.Exists(path)) return System.IO.File.ReadAllText(path);
        }
        catch { /* ignore */ }

        return null;
    }

    /// <summary>Light cleanup of GitHub's markdown release bodies for plain-text display.</summary>
    private static string CleanBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var outLines = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("**Full Changelog**")) continue;
            line = line.TrimStart('#').Trim();
            line = line.Replace("* ", "\u2022 ");
            outLines.Add(line);
        }
        var text = string.Join("\n", outLines).Trim();
        if (text.Length > 900) text = text[..900].TrimEnd() + "\u2026";
        return text;
    }

    /// <summary>Renders the bundled CHANGELOG.md as curated "What's new" sections.
    /// Returns false if the file is missing or empty so callers can fall back.</summary>
    private bool TryRenderLocalChangelog()
    {
        string? text = ReadBundledChangelog();
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var sections = new List<(string Header, List<string> Bullets)>();
        (string Header, List<string> Bullets)? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("## "))
            {
                if (current is { } c) sections.Add(c);
                current = (line[3..].Trim(), new List<string>());
            }
            else if (current is { } cur && (line.StartsWith("- ") || line.StartsWith("* ")))
            {
                cur.Bullets.Add("\u2022 " + line[2..].Trim());
            }
        }
        if (current is { } last) sections.Add(last);
        if (sections.Count == 0) return false;

        ChangelogPanel.Children.Clear();
        bool first = true;
        foreach (var (header, bullets) in sections)
        {
            ChangelogPanel.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, first ? 0 : 12, 0, 0),
            });
            first = false;
            if (bullets.Count > 0)
            {
                ChangelogPanel.Children.Add(new TextBlock
                {
                    Text = string.Join("\n", bullets),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                    Foreground = secondary,
                });
            }
        }
        return true;
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser available */ }
    }
}
