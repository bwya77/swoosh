using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using Swoosh.Native;
using Swoosh.Settings;
using Swoosh.Updates;

namespace Swoosh.UI;

/// <summary>Polished settings window: general toggles, the thirds modifier,
/// version, update check, and a live changelog from GitHub Releases.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly UpdateChecker _updates;
    private bool _loading;
    private string? _downloadUrl;
    private string _overlayColor = "#0A84FF";

    private static readonly string[] SwatchColors =
        { "#0A84FF", "#5AC8FA", "#34C759", "#AF52DE", "#FF2D55", "#FF9500", "#FFD60A", "#8E8E93" };
    private readonly List<System.Windows.Controls.Border> _swatches = new();

    private static readonly System.Windows.Media.Brush Subtle = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xA6, 0xA6));

    public SettingsWindow(SettingsStore store, UpdateChecker updates)
    {
        _store = store;
        _updates = updates;
        InitializeComponent();

        VersionPill.Text = $"v{_updates.CurrentVersion}";
        BuildSwatches();
        LoadFrom(_store.Current);

        GesturesToggle.Checked += OnAnyChanged; GesturesToggle.Unchecked += OnAnyChanged;
        AnimateToggle.Checked += OnAnyChanged; AnimateToggle.Unchecked += OnAnyChanged;
        DebugToggle.Checked += OnAnyChanged; DebugToggle.Unchecked += OnAnyChanged;
        GridToggle.Checked += OnGridChanged; GridToggle.Unchecked += OnGridChanged;
        ModifierCombo.SelectionChanged += OnAnyChanged;
        SensitivitySlider.ValueChanged += OnAnyChanged;
        OverlayAccentToggle.Checked += OnAccentChanged; OverlayAccentToggle.Unchecked += OnAccentChanged;

        CheckBtn.Click += async (_, _) => await RunUpdateCheck();
        DownloadBtn.Click += (_, _) => OpenUrl(_downloadUrl);

        Nav.SelectionChanged += (_, _) => ShowPane(Nav.SelectedIndex);
        Nav.SelectedIndex = 0;

        _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;

        Loaded += async (_, _) =>
        {
            await RunUpdateCheck();
            await LoadChangelog();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.EnableMicaDark(hwnd); // dark titlebar + Mica on Win11 (no-op otherwise)
    }

    private void LoadFrom(AppSettings s)
    {
        _loading = true;
        GesturesToggle.IsChecked = s.GesturesEnabled;
        AnimateToggle.IsChecked = s.AnimateSnaps;
        DebugToggle.IsChecked = s.DebugOverlay;
        GridToggle.IsChecked = s.GridModifierEnabled;
        ModifierCombo.SelectedIndex = s.GridModifier switch
        {
            GridModifier.Ctrl => 1,
            GridModifier.Alt => 2,
            _ => 0,
        };
        ModifierCombo.IsEnabled = s.GridModifierEnabled;
        SensitivitySlider.Value = s.Sensitivity;
        OverlayAccentToggle.IsChecked = s.OverlayUseAccent;
        _overlayColor = s.OverlayColor;
        HighlightSwatch(_overlayColor);
        SetSwatchesEnabled(!s.OverlayUseAccent);
        _loading = false;
    }

    private void BuildSwatches()
    {
        foreach (var hex in SwatchColors)
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var sw = new System.Windows.Controls.Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(2.5),
                Tag = hex,
                ToolTip = hex,
            };
            sw.MouseLeftButtonUp += (_, _) => OnSwatchPicked(hex);
            _swatches.Add(sw);
            SwatchPanel.Children.Add(sw);
        }
    }

    private void HighlightSwatch(string hex)
    {
        foreach (var sw in _swatches)
        {
            bool sel = string.Equals((string)sw.Tag, hex, StringComparison.OrdinalIgnoreCase);
            sw.BorderBrush = sel
                ? System.Windows.Media.Brushes.White
                : System.Windows.Media.Brushes.Transparent;
        }
    }

    private void SetSwatchesEnabled(bool enabled)
    {
        SwatchPanel.IsEnabled = enabled;
        SwatchPanel.Opacity = enabled ? 1.0 : 0.4;
    }

    private void OnSwatchPicked(string hex)
    {
        _overlayColor = hex;
        HighlightSwatch(hex);
        if (_loading) return;
        // Picking a color implies a custom color, so leave the accent mode off.
        OverlayAccentToggle.IsChecked = false;
        _store.Save(Collect());
    }

    private void OnAccentChanged(object sender, RoutedEventArgs e)
    {
        SetSwatchesEnabled(OverlayAccentToggle.IsChecked != true);
        if (_loading) return;
        _store.Save(Collect());
    }

    private AppSettings Collect() => new()
    {
        GesturesEnabled = GesturesToggle.IsChecked == true,
        AnimateSnaps = AnimateToggle.IsChecked == true,
        DebugOverlay = DebugToggle.IsChecked == true,
        GridModifierEnabled = GridToggle.IsChecked == true,
        GridModifier = ModifierCombo.SelectedIndex switch
        {
            1 => GridModifier.Ctrl,
            2 => GridModifier.Alt,
            _ => GridModifier.Shift,
        },
        Sensitivity = SensitivitySlider.Value,
        OverlayUseAccent = OverlayAccentToggle.IsChecked == true,
        OverlayColor = _overlayColor,
    };

    private void OnAnyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _store.Save(Collect());
    }

    private void OnGridChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ModifierCombo.IsEnabled = GridToggle.IsChecked == true;
        _store.Save(Collect());
    }

    private void OnStoreChanged(AppSettings s)
    {
        // Another surface (e.g. the tray) changed settings: mirror it here.
        Dispatcher.Invoke(() => LoadFrom(s));
    }

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

    private void ShowPane(int index)
    {
        if (index < 0) index = 0;
        GeneralPane.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnappingPane.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPane.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        AboutPane.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadChangelog()
    {
        var releases = await _updates.ReleasesAsync(1);
        ChangelogPanel.Children.Clear();

        if (releases.Count == 0)
        {
            ChangelogPanel.Children.Add(new TextBlock
            {
                Text = "Couldn't load release notes (offline or rate-limited).",
                Foreground = Subtle,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var r = releases[0];

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = r.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
        if (r.Published is { } p)
        {
            var date = new TextBlock
            {
                Text = p.LocalDateTime.ToString("MMM d, yyyy"),
                FontSize = 11,
                Foreground = Subtle,
                VerticalAlignment = VerticalAlignment.Center,
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
                Foreground = Subtle,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
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
            if (line.StartsWith("**Full Changelog**")) continue; // noisy compare link
            line = line.TrimStart('#').Trim();                    // drop markdown headers
            line = line.Replace("* ", "\u2022 ");                 // bullets
            outLines.Add(line);
        }
        var text = string.Join("\n", outLines).Trim();
        if (text.Length > 900) text = text[..900].TrimEnd() + "\u2026";
        return text;
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser available */ }
    }
}
