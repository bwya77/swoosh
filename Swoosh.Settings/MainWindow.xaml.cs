using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
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
    private Button? _customSwatch;
    private Canvas? _svBox;
    private Border? _svHueLayer;
    private Ellipse? _svThumb;
    private Canvas? _hueBar;
    private Border? _hueThumb;
    private TextBlock? _rgbReadout;
    private TextBox? _hexBox;
    private Border? _previewBox;
    private double _hue, _sat, _val;
    private bool _svActive, _hueActive;
    private bool _suppressColorEdit;

    private const double SvW = 220, SvH = 140, HueW = 220, HueH = 16, ThumbSize = 14;

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
    private readonly List<InstalledAppEntry> _installedApps = new();
    private readonly HashSet<string> _selectedCompatibilityApps = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingAdditionalApps;
    private TextBlock? _minimizeCardTitle; // the "Swipe down" card's title, retitled per SwipeDownAction

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
        ResizeForDpi(1060, 840);
        EnforceMinimumSize(600, 480);
        TrySetWindowIcon();

        UpdateCaptionButtonColors();
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            UpdateCaptionButtonColors();
            HighlightSwatch(_overlayColor);
            RefreshSecondaryTexts();
        };

        // Adaptive header: hide the Home status chips when the window is too narrow to
        // show them without crowding the title, mirroring how Settings reflows. Done in
        // code because AdaptiveTrigger is unreliable for content nested in a NavigationView.
        RootGrid.SizeChanged += (_, e) => UpdateHeaderAdaptive(e.NewSize.Width);

        VersionText.Text = $"v{_updates.CurrentVersion}";
        HeroVersion.Text = $"v{_updates.CurrentVersion}";
        TrySetAboutLogo();
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
            UpdateHeaderAdaptive(RootGrid.ActualWidth);

            await RunUpdateCheck();
            await LoadInstalledApps();
        };
    }

    /// <summary>Render the lifetime swoosh tally in the nav pane footer and the General
    /// hero header, with a thousands separator and singular/plural label.</summary>
    private void UpdateSwooshCount(long n)
    {
        string num = n.ToString("N0");
        string label = n == 1 ? "lifetime swoosh" : "lifetime swooshes";
        SwooshCountText.Text = num;
        SwooshCountLabel.Text = label;
        HeroCountText.Text = num;
        HeroCountLabel.Text = label;
    }

    /// <summary>Collapse the Home header status chips when the window is too narrow to fit
    /// them beside the title, so the title and tagline keep a sensible width instead of
    /// wrapping one character per line.</summary>
    private void UpdateHeaderAdaptive(double width)
    {
        if (HeaderStats != null)
            HeaderStats.Visibility = width >= 820 ? Visibility.Visible : Visibility.Collapsed;
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

    // Enforce a minimum window size so the content never gets squeezed so narrow that the
    // settings rows wrap one character per line. Done with a window subclass that handles
    // WM_GETMINMAXINFO, the standard Win32 way to set a minimum track size.
    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private static int _minWinW = 640, _minWinH = 480;
    private static IntPtr _origWndProc = IntPtr.Zero;
    private static WndProcDelegate? _wndProcHolder;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrRaw(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    /// <summary>Subclass the window to clamp its minimum track size (DPI-scaled).</summary>
    private void EnforceMinimumSize(int logicalWidth, int logicalHeight)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        _minWinW = (int)Math.Round(logicalWidth * scale);
        _minWinH = (int)Math.Round(logicalHeight * scale);

        _wndProcHolder = SubclassProc;
        _origWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, _wndProcHolder);
    }

    private static IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.X = _minWinW;
            mmi.ptMinTrackSize.Y = _minWinH;
            System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
        }
        return CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
    }

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

    /// <summary>Show the Swoosh logo on the About page and the General hero header from the
    /// PNG copied next to the executable. Loaded by file path (not ms-appx) because this is
    /// an unpackaged app.</summary>
    private void TrySetAboutLogo()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "swoosh-256.png");
            if (System.IO.File.Exists(path))
            {
                var src = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
                AppLogo.Source = src;
                GeneralLogo.Source = src;
            }
        }
        catch { /* non-fatal: the cards just show no logo */ }
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
        SnapSpeedSlider.Value = Math.Clamp((s.SnapAnimationSeconds * 1000.0 - 50) / 10.0, 0, 35);
        DemoToggle.IsOn = s.DemoOverlay;
        PhantomToggle.IsOn = s.PhantomRejection;
        ModifierCombo.SelectedIndex = s.GridModifier switch
        {
            GridModifier.Ctrl => 1,
            GridModifier.Alt => 2,
            _ => 0,
        };
        ModifierCombo.IsEnabled = s.GridModifierEnabled;
        SwipeDownCombo.SelectedIndex = s.SwipeDownAction switch
        {
            SwipeDownMode.Close => 1,
            SwipeDownMode.Choose => 2,
            _ => 0,
        };
        // Slider runs 0..28 (Minimum must be 0 or WinUI throws at parse time); the real percent is
        // value + 2, i.e. a 2%..30% pull. Threshold is stored as a 0..1 fraction.
        SwipeDownThresholdSlider.Value = Math.Clamp(s.SwipeDownThreshold * 100.0 - 2, 0, 28);
        UpdateMinimizeCardTitle();
        SensitivitySlider.Value = s.Sensitivity;
        // Move to display is one control: Off, or the modifier key that engages it.
        MonitorMoveCombo.SelectedIndex = !s.MonitorMoveEnabled ? 0 : s.MonitorMoveModifier switch
        {
            GridModifier.Shift => 1,
            GridModifier.Ctrl => 2,
            GridModifier.Alt => 3,
            _ => 1,
        };
        AppCompatibilityModeCombo.SelectedIndex = s.AppCompatibilityMode == AppCompatibilityMode.RequireModifier ? 1 : 0;
        AppCompatibilityModifierCombo.SelectedIndex = ModifierIndex(s.AppCompatibilityModifier);
        LoadCompatibilityApps(s.AppCompatibilityProcessNames);
        UpdateAppCompatibilityControls();
        OverlayAccentToggle.IsOn = s.OverlayUseAccent;
        HudBackgroundCombo.SelectedIndex = s.HudBackground switch
        {
            HudTheme.Light => 1,
            HudTheme.System => 2,
            _ => 0,
        };
        HudSizeCombo.SelectedIndex = s.HudSize == HudSize.Large ? 1 : 0;
        // Same Minimum-0-plus-0.1-offset trick as the hold-delay slider: the fade duration is
        // the slider value plus a 0.1s floor, so the usable range is 0.1 to 1.5s.
        HudFadeSlider.Value = Math.Clamp(s.HudFadeOutSeconds - 0.1, 0.0, 1.4);
        UpdateHudFadeLabel(Math.Clamp(s.HudFadeOutSeconds, 0.1, 1.5));
        _overlayColor = s.OverlayColor;
        HighlightSwatch(_overlayColor);
        SetSwatchesEnabled(!s.OverlayUseAccent);
        GridSpacingSlider.Value = Math.Clamp(s.GridSpacing, 0, 10);
        UpdateGridSpacingLabel(GridSpacingSlider.Value);
        CancelTimeoutSlider.Value = Math.Clamp(s.CancelTimeoutSeconds, 0, 3);
        UpdateCancelTimeoutLabel(CancelTimeoutSlider.Value);
        LivePreviewToggle.IsOn = s.LivePreview;
        MoveCursorToggle.IsOn = s.MoveCursor;
        MouseHudToggle.IsOn = s.MouseMiddleButtonHudEnabled;
        ResizeHorizontalToggle.IsOn = s.ResizeHorizontalEnabled;
        ResizeVerticalToggle.IsOn = s.ResizeVerticalEnabled;
        FiveFingerToggle.IsOn = s.FiveFingerEnabled;
        PreviewDesktopDestinationToggle.IsOn = s.PreviewDesktopDestination;
        CreateDesktopOverflowToggle.IsOn = s.CreateDesktopOnOverflow;
        AppSwitchOnHoldToggle.IsOn = s.AppSwitchOnHold;
        // The slider's Minimum is 0 (so its default value needs no coercion, which crashes
        // WinUI when a ValueChanged handler is attached); the delay is the slider value plus
        // a 0.1s floor, so the usable range is 0.1 to 1.0s.
        HoldDelaySlider.Value = Math.Clamp(s.DesktopHoldDelaySeconds - 0.1, 0.0, 0.9);
        UpdateHoldDelayLabel(Math.Clamp(s.DesktopHoldDelaySeconds, 0.1, 1.0));
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
        SnapAnimationSeconds = (50 + SnapSpeedSlider.Value * 10) / 1000.0,
        DemoOverlay = DemoToggle.IsOn,
        PhantomRejection = PhantomToggle.IsOn,
        GridModifierEnabled = GestureOn("thirds"),
        GridModifier = ModifierCombo.SelectedIndex switch
        {
            1 => GridModifier.Ctrl,
            2 => GridModifier.Alt,
            _ => GridModifier.Shift,
        },
        Sensitivity = SensitivitySlider.Value,
        SwipeDownAction = SwipeDownCombo.SelectedIndex switch
        {
            1 => SwipeDownMode.Close,
            2 => SwipeDownMode.Choose,
            _ => SwipeDownMode.Minimize,
        },
        SwipeDownThreshold = (SwipeDownThresholdSlider.Value + 2) / 100.0,
        MonitorMoveEnabled = MonitorMoveCombo.SelectedIndex > 0,
        MonitorMoveModifier = MonitorMoveCombo.SelectedIndex switch
        {
            2 => GridModifier.Ctrl,
            3 => GridModifier.Alt,
            _ => GridModifier.Shift,
        },
        AppCompatibilityMode = AppCompatibilityModeCombo.SelectedIndex == 1
            ? AppCompatibilityMode.RequireModifier
            : AppCompatibilityMode.Exclude,
        AppCompatibilityModifier = ModifierFromIndex(AppCompatibilityModifierCombo.SelectedIndex),
        AppCompatibilityProcessNames = CollectCompatibilityApps().ToList(),
        OverlayUseAccent = OverlayAccentToggle.IsOn,
        HudBackground = HudBackgroundCombo.SelectedIndex switch
        {
            1 => HudTheme.Light,
            2 => HudTheme.System,
            _ => HudTheme.Dark,
        },
        HudSize = HudSizeCombo.SelectedIndex == 1 ? HudSize.Large : HudSize.Normal,
        HudFadeOutSeconds = HudFadeSlider.Value + 0.1,
        OverlayColor = _overlayColor,
        GridSpacing = (int)Math.Round(GridSpacingSlider.Value),
        CancelTimeoutSeconds = CancelTimeoutSlider.Value,
        LivePreview = LivePreviewToggle.IsOn,
        MoveCursor = MoveCursorToggle.IsOn,
        MouseMiddleButtonHudEnabled = MouseHudToggle.IsOn,
        ResizeHorizontalEnabled = ResizeHorizontalToggle.IsOn,
        ResizeVerticalEnabled = ResizeVerticalToggle.IsOn,
        FiveFingerEnabled = FiveFingerToggle.IsOn,
        PreviewDesktopDestination = PreviewDesktopDestinationToggle.IsOn,
        CreateDesktopOnOverflow = CreateDesktopOverflowToggle.IsOn,
        AppSwitchOnHold = AppSwitchOnHoldToggle.IsOn,
        DesktopHoldDelaySeconds = HoldDelaySlider.Value + 0.1,
    };

    private void SaveIfReady()
    {
        if (_loading) return;
        _store.Save(Collect());
    }

    // ---- Control events ----------------------------------------------------

    private void OnSettingToggled(object sender, RoutedEventArgs e) => SaveIfReady();

    private void OnModifierChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAppCompatibilityControls();
        SaveIfReady();
    }
    private void OnSwipeDownChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateMinimizeCardTitle();
        SaveIfReady();
    }

    // Retitle the swipe-down gesture tile to match the configured action so the card doesn't
    // misleadingly say "Minimize" when it is set to close (or to let the user choose).
    private void UpdateMinimizeCardTitle()
    {
        if (_minimizeCardTitle == null) return;
        _minimizeCardTitle.Text = SwipeDownCombo.SelectedIndex switch
        {
            1 => "Close",
            2 => "Minimize / Close",
            _ => "Minimize",
        };
    }

    private void OnSwipeDownThresholdChanged(object sender, RangeBaseValueChangedEventArgs e) => SaveIfReady();

    private void OnMonitorMoveChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAppCompatibilityControls();
        SaveIfReady();
    }

    private void OnAppCompatibilityChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAppCompatibilityControls();
        SaveIfReady();
    }

    private void OnAppCompatibilityAppsChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingAdditionalApps) return;
        SaveIfReady();
    }

    private void OnInstalledAppsSearchChanged(object sender, TextChangedEventArgs e) => RefreshInstalledAppsList();

    private async void OnRefreshInstalledApps(object sender, RoutedEventArgs e) => await LoadInstalledApps();

    private void OnHudBackgroundChanged(object sender, SelectionChangedEventArgs e) => SaveIfReady();

    private void OnHudSizeChanged(object sender, SelectionChangedEventArgs e) => SaveIfReady();

    private void OnSensitivityChanged(object sender, RangeBaseValueChangedEventArgs e) => SaveIfReady();

    private void OnSnapSpeedChanged(object sender, RangeBaseValueChangedEventArgs e) => SaveIfReady();

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

    private void OnHoldDelayChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateHoldDelayLabel(e.NewValue + 0.1);
        SaveIfReady();
    }

    private void OnHudFadeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateHudFadeLabel(e.NewValue + 0.1);
        SaveIfReady();
    }

    // A fresh AppSettings supplies the default values used by "Restore defaults".
    private static readonly AppSettings Defaults = new();

    /// <summary>Reset every setting to its default and persist it, after a confirmation prompt.</summary>
    private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Restore defaults?",
            Content = "This resets all Swoosh settings to their original values. Your gestures keep working; only your customizations are cleared.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _store.Save(Defaults);
        LoadFrom(Defaults);
    }

    /// <summary>Open a pre-filled GitHub issue with the diagnostics report attached, so beta users
    /// can report a problem in one click.</summary>
    private void OnReportProblem(object sender, RoutedEventArgs e)
    {
        string diag;
        try { diag = System.IO.File.ReadAllText(DiagnosticsPath); }
        catch { diag = "(diagnostics unavailable - make sure Swoosh is running)"; }

        string body =
            "## What happened?\n\n_Describe the problem and the steps to reproduce it._\n\n" +
            "## Diagnostics\n```\n" + diag + "\n```\n";
        string url = "https://github.com/bwya77/swoosh/issues/new?labels=beta" +
                     "&title=" + Uri.EscapeDataString("[Beta] ") +
                     "&body=" + Uri.EscapeDataString(body);
        OpenUrl(url);
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

    private void UpdateHoldDelayLabel(double delaySeconds)
    {
        if (HoldDelayValue != null) HoldDelayValue.Text = $"{delaySeconds:0.00} s";
    }

    private void UpdateHudFadeLabel(double fadeSeconds)
    {
        if (HudFadeValue != null) HudFadeValue.Text = $"{fadeSeconds:0.00} s";
    }

    private void UpdateAppCompatibilityControls()
    {
        if (AppCompatibilityModifierRow == null || AppCompatibilityWarning == null) return;

        bool requireModifier = AppCompatibilityModeCombo.SelectedIndex == 1;
        AppCompatibilityModifierRow.Visibility = requireModifier ? Visibility.Visible : Visibility.Collapsed;

        if (!requireModifier)
        {
            AppCompatibilityWarning.IsOpen = false;
            return;
        }

        var modifier = ModifierFromIndex(AppCompatibilityModifierCombo.SelectedIndex);
        var conflicts = new List<string>();
        if (GestureOn("thirds") && ModifierFromIndex(ModifierCombo.SelectedIndex) == modifier)
            conflicts.Add("thirds snapping");
        if (MonitorMoveCombo.SelectedIndex > 0 && ModifierFromMonitorIndex(MonitorMoveCombo.SelectedIndex) == modifier)
            conflicts.Add("move to display");

        AppCompatibilityWarning.IsOpen = conflicts.Count > 0;
        if (conflicts.Count > 0)
        {
            AppCompatibilityWarning.Message =
                $"{ModifierLabel(modifier)} is also used for {string.Join(" and ", conflicts)}. It will still save, but those gestures may take precedence while the key is held.";
        }
    }

    private void LoadCompatibilityApps(IEnumerable<string> processNames)
    {
        var selected = AppCompatibility.ParseProcessList(string.Join(Environment.NewLine, processNames))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _selectedCompatibilityApps.Clear();
        foreach (string name in selected)
            _selectedCompatibilityApps.Add(name);

        SyncAdditionalAppsBox();
        RefreshInstalledAppsList();
    }

    private IReadOnlyList<string> CollectCompatibilityApps()
    {
        var names = new List<string>(_selectedCompatibilityApps);
        names.AddRange(AppCompatibility.ParseProcessList(AppCompatibilityAppsBox.Text));
        return names
            .Select(AppCompatibility.NormalizeProcessName)
            .Where(static name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IEnumerable<string> AdditionalCompatibilityApps()
    {
        var installed = _installedApps
            .Select(static app => app.ProcessName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _selectedCompatibilityApps.Where(name => !installed.Contains(name));
    }

    private void SyncAdditionalAppsBox()
    {
        _syncingAdditionalApps = true;
        try
        {
            AppCompatibilityAppsBox.Text = AppCompatibility.FormatProcessList(AdditionalCompatibilityApps());
        }
        finally
        {
            _syncingAdditionalApps = false;
        }
    }

    private void SetCompatibilityAppSelected(string processName, bool selected)
    {
        processName = AppCompatibility.NormalizeProcessName(processName);
        if (processName.Length == 0) return;

        if (selected)
            _selectedCompatibilityApps.Add(processName);
        else
            _selectedCompatibilityApps.Remove(processName);
    }

    private async Task LoadInstalledApps()
    {
        if (InstalledAppsStatus == null) return;

        InstalledAppsStatus.Text = "Loading installed apps...";
        RefreshInstalledAppsButton.IsEnabled = false;

        try
        {
            var apps = await Task.Run(InstalledAppCatalog.Load);
            _installedApps.Clear();
            _installedApps.AddRange(apps);
            SyncAdditionalAppsBox();
            RefreshInstalledAppsList();
        }
        catch
        {
            InstalledAppsStatus.Text = "Installed apps could not be loaded.";
        }
        finally
        {
            RefreshInstalledAppsButton.IsEnabled = true;
        }
    }

    private void RefreshInstalledAppsList()
    {
        if (InstalledAppsList == null || InstalledAppsStatus == null) return;

        string query = InstalledAppsSearchBox?.Text?.Trim() ?? "";
        var filtered = _installedApps
            .Where(app => query.Length == 0 ||
                          app.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                          app.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .ToArray();

        InstalledAppsList.Items.Clear();
        foreach (var app in filtered)
            InstalledAppsList.Items.Add(BuildInstalledAppRow(app));

        InstalledAppsStatus.Text = _installedApps.Count == 0
            ? "No installed apps found yet."
            : filtered.Length == 0
                ? "No apps match your search."
                : $"Showing {filtered.Length:N0} of {_installedApps.Count:N0} installed apps.";
    }

    private FrameworkElement BuildInstalledAppRow(InstalledAppEntry app)
    {
        var check = new CheckBox
        {
            IsChecked = _selectedCompatibilityApps.Contains(app.ProcessName),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
            Padding = new Thickness(0),
        };
        check.Checked += (_, _) => { SetCompatibilityAppSelected(app.ProcessName, true); SaveIfReady(); };
        check.Unchecked += (_, _) => { SetCompatibilityAppSelected(app.ProcessName, false); SaveIfReady(); };

        var icon = new Image
        {
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 12, 0),
        };
        if (app.IconPath.Length > 0 && File.Exists(app.IconPath))
            icon.Source = new BitmapImage(new Uri(app.IconPath));

        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = app.DisplayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = app.ProcessName,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = SecondaryTextBrush(),
        });

        var row = new Grid { Padding = new Thickness(4, 6, 4, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(check, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(text, 2);
        row.Children.Add(check);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    private static GridModifier ModifierFromIndex(int index) => index switch
    {
        1 => GridModifier.Ctrl,
        2 => GridModifier.Alt,
        _ => GridModifier.Shift,
    };

    private static int ModifierIndex(GridModifier modifier) => modifier switch
    {
        GridModifier.Ctrl => 1,
        GridModifier.Alt => 2,
        _ => 0,
    };

    private static GridModifier ModifierFromMonitorIndex(int index) => index switch
    {
        2 => GridModifier.Ctrl,
        3 => GridModifier.Alt,
        _ => GridModifier.Shift,
    };

    private static string ModifierLabel(GridModifier modifier) => modifier switch
    {
        GridModifier.Ctrl => "Ctrl",
        GridModifier.Alt => "Alt",
        _ => "Shift",
    };

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

        BuildCustomSwatch();
    }

    /// <summary>A trailing swatch that opens a lightweight color editor (live preview, hex box,
    /// and R/G/B sliders) so the user can choose any color beyond the presets. The native WinUI
    /// ColorPicker spectrum is laggy on this hardware, so we use sliders which drag smoothly.
    /// The swatch fills with the active custom color and is ringed when the current overlay color
    /// is not one of the presets.</summary>
    private void BuildCustomSwatch()
    {
        var icon = new FontIcon { Glyph = "\uE790", FontSize = 15 };

        var btn = new Button
        {
            Width = 32,
            Height = 32,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(2.5),
            BorderBrush = UnselectedSwatchStroke(),
            Content = icon,
        };
        ToolTipService.SetToolTip(btn, "Custom color");

        var flyout = new Flyout { Content = BuildColorEditor() };
        // Sync the editor to the current overlay color each time it opens.
        flyout.Opened += (_, _) => SyncEditorTo(_overlayColor);
        btn.Flyout = flyout;

        _customSwatch = btn;
        SwatchPanel.Children.Add(btn);
    }

    private FrameworkElement BuildColorEditor()
    {
        var panel = new StackPanel { Spacing = 12, Width = SvW };

        // ---- Saturation/Value field (click or drag anywhere) ----
        _svBox = new Canvas { Width = SvW, Height = SvH };

        _svHueLayer = new Border
        {
            Width = SvW,
            Height = SvH,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Red),
        };

        // White (left, opaque) -> transparent (right): saturation.
        var satOverlay = new Border
        {
            Width = SvW,
            Height = SvH,
            CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = Color.FromArgb(255, 255, 255, 255) },
                    new GradientStop { Offset = 1, Color = Color.FromArgb(0, 255, 255, 255) },
                },
            },
        };

        // Transparent (top) -> black (bottom): value.
        var valOverlay = new Border
        {
            Width = SvW,
            Height = SvH,
            CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = Color.FromArgb(0, 0, 0, 0) },
                    new GradientStop { Offset = 1, Color = Color.FromArgb(255, 0, 0, 0) },
                },
            },
        };

        _svThumb = new Ellipse
        {
            Width = ThumbSize,
            Height = ThumbSize,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false,
        };

        _svBox.Children.Add(_svHueLayer);
        _svBox.Children.Add(satOverlay);
        _svBox.Children.Add(valOverlay);
        _svBox.Children.Add(_svThumb);

        _svBox.PointerPressed += (s, e) => { _svActive = true; _svBox.CapturePointer(e.Pointer); SetSvFromPointer(e); };
        _svBox.PointerMoved += (s, e) => { if (_svActive) SetSvFromPointer(e); };
        _svBox.PointerReleased += (s, e) => { if (_svActive) { _svActive = false; _svBox.ReleasePointerCapture(e.Pointer); EditHsv(persist: true); } };
        _svBox.PointerCanceled += (s, e) => { if (_svActive) { _svActive = false; EditHsv(persist: true); } };
        panel.Children.Add(_svBox);

        // ---- Hue bar ----
        _hueBar = new Canvas { Width = HueW, Height = HueH };
        var hueFill = new Border
        {
            Width = HueW,
            Height = HueH,
            CornerRadius = new CornerRadius(4),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Offset = 0.0,    Color = Color.FromArgb(255, 255, 0, 0) },
                    new GradientStop { Offset = 0.1667, Color = Color.FromArgb(255, 255, 255, 0) },
                    new GradientStop { Offset = 0.3333, Color = Color.FromArgb(255, 0, 255, 0) },
                    new GradientStop { Offset = 0.5,    Color = Color.FromArgb(255, 0, 255, 255) },
                    new GradientStop { Offset = 0.6667, Color = Color.FromArgb(255, 0, 0, 255) },
                    new GradientStop { Offset = 0.8333, Color = Color.FromArgb(255, 255, 0, 255) },
                    new GradientStop { Offset = 1.0,    Color = Color.FromArgb(255, 255, 0, 0) },
                },
            },
        };
        _hueThumb = new Border
        {
            Width = 6,
            Height = HueH,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Colors.White),
            IsHitTestVisible = false,
        };
        _hueBar.Children.Add(hueFill);
        _hueBar.Children.Add(_hueThumb);

        _hueBar.PointerPressed += (s, e) => { _hueActive = true; _hueBar.CapturePointer(e.Pointer); SetHueFromPointer(e); };
        _hueBar.PointerMoved += (s, e) => { if (_hueActive) SetHueFromPointer(e); };
        _hueBar.PointerReleased += (s, e) => { if (_hueActive) { _hueActive = false; _hueBar.ReleasePointerCapture(e.Pointer); EditHsv(persist: true); } };
        _hueBar.PointerCanceled += (s, e) => { if (_hueActive) { _hueActive = false; EditHsv(persist: true); } };
        panel.Children.Add(_hueBar);

        // ---- Preview + hex ----
        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _previewBox = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = UnselectedSwatchStroke(),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(ParseColor(_overlayColor)),
        };
        Grid.SetColumn(_previewBox, 0);

        _hexBox = new TextBox
        {
            Header = "Hex",
            PlaceholderText = "#RRGGBB",
            MaxLength = 7,
        };
        _hexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) ApplyHex();
        };
        _hexBox.LostFocus += (_, _) => ApplyHex();
        Grid.SetColumn(_hexBox, 1);

        bottom.Children.Add(_previewBox);
        bottom.Children.Add(_hexBox);
        panel.Children.Add(bottom);

        _rgbReadout = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = SecondaryTextBrush(),
        };
        panel.Children.Add(_rgbReadout);

        return panel;
    }

    private void SetSvFromPointer(PointerRoutedEventArgs e)
    {
        if (_svBox == null) return;
        var p = e.GetCurrentPoint(_svBox).Position;
        _sat = Clamp01(p.X / SvW);
        _val = Clamp01(1 - p.Y / SvH);
        EditHsv(persist: false); // live preview while dragging; persist on release
    }

    private void SetHueFromPointer(PointerRoutedEventArgs e)
    {
        if (_hueBar == null) return;
        var p = e.GetCurrentPoint(_hueBar).Position;
        _hue = Clamp01(p.X / HueW) * 360.0;
        EditHsv(persist: false);
    }

    /// <summary>Push the current H/S/V state to the editor's visuals only (thumbs, hue base,
    /// preview, hex box, RGB readout). Cheap and allocation-light, safe to call on every
    /// pointer move. Does not change saved state.</summary>
    private void RenderHsv()
    {
        var c = FromHsv(_hue, _sat, _val);
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        _suppressColorEdit = true;
        if (_svHueLayer != null) _svHueLayer.Background = new SolidColorBrush(FromHsv(_hue, 1, 1));
        if (_svThumb != null)
        {
            Canvas.SetLeft(_svThumb, _sat * SvW - ThumbSize / 2);
            Canvas.SetTop(_svThumb, (1 - _val) * SvH - ThumbSize / 2);
        }
        if (_hueThumb != null)
            Canvas.SetLeft(_hueThumb, Clamp01(_hue / 360.0) * HueW - 3);
        if (_previewBox != null) _previewBox.Background = new SolidColorBrush(c);
        if (_hexBox != null) _hexBox.Text = hex;
        if (_rgbReadout != null) _rgbReadout.Text = $"R {c.R}   G {c.G}   B {c.B}";
        _suppressColorEdit = false;
    }

    /// <summary>Apply the current H/S/V as the chosen overlay color. Always updates the editor
    /// visuals and the live in-memory color; only writes to disk when <paramref name="persist"/>
    /// is true. Dragging calls this with persist:false on every move (smooth, no disk I/O) and
    /// persist:true once on release, so a fast drag through many colors no longer thrashes
    /// settings.json or fights the file-watcher echo.</summary>
    private void EditHsv(bool persist)
    {
        RenderHsv();
        var c = FromHsv(_hue, _sat, _val);
        _overlayColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        if (!persist) return;

        // Choosing a custom color implies the accent mode should be off.
        OverlayAccentToggle.IsOn = false;
        HighlightSwatch(_overlayColor);
        if (_loading) return;
        _store.Save(Collect());
    }

    private void ApplyHex()
    {
        if (_suppressColorEdit || _hexBox == null) return;
        if (!TryParseHex(_hexBox.Text, out var c))
        {
            // Reject invalid input: restore to the current color.
            SyncEditorTo(_overlayColor);
            return;
        }
        ToHsv(c, out _hue, out _sat, out _val);
        EditHsv(persist: true);
    }

    private void SyncEditorTo(string hex)
    {
        ToHsv(ParseColor(hex), out _hue, out _sat, out _val);
        RenderHsv(); // visuals only: opening the editor must not flip accent off or save
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromArgb(255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static void ToHsv(Color col, out double h, out double s, out double v)
    {
        double r = col.R / 255.0, g = col.G / 255.0, b = col.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) h = 0;
        else if (max == r) h = 60 * ((((g - b) / d) % 6 + 6) % 6);
        else if (max == g) h = 60 * (((b - r) / d) + 2);
        else h = 60 * (((r - g) / d) + 4);
    }

    private static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6) return false;
        try
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            color = Color.FromArgb(255, r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void HighlightSwatch(string hex)
    {
        // The selection ring must contrast with the window background, not just the swatch:
        // a white ring vanishes against the light-mode backdrop. Use white on dark themes
        // and a near-black ring on light themes.
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var selBrush = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 0, 0, 0));
        bool isPreset = false;
        foreach (var sw in _swatches)
        {
            bool sel = string.Equals((string)sw.Tag, hex, StringComparison.OrdinalIgnoreCase);
            if (sel) isPreset = true;
            sw.BorderBrush = sel ? selBrush : UnselectedSwatchStroke();
        }

        // The custom swatch is "selected" whenever the active color is not a preset. Fill it with
        // the chosen color (with a contrasting glyph) so the picked color is unmistakable.
        if (_customSwatch != null)
        {
            if (isPreset)
            {
                _customSwatch.Background = new SolidColorBrush(Colors.Transparent);
                _customSwatch.Resources.Remove("ButtonBackground");
                _customSwatch.Resources.Remove("ButtonBackgroundPointerOver");
                _customSwatch.Resources.Remove("ButtonBackgroundPressed");
                _customSwatch.Resources.Remove("ButtonBackgroundDisabled");
                _customSwatch.BorderBrush = UnselectedSwatchStroke();
                if (_customSwatch.Content is FontIcon fi) fi.Foreground = SecondaryTextBrush();
            }
            else
            {
                var c = ParseColor(hex);
                _customSwatch.Background = new SolidColorBrush(c);
                _customSwatch.Resources["ButtonBackground"] = new SolidColorBrush(c);
                _customSwatch.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(c);
                _customSwatch.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(c);
                _customSwatch.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(c);
                _customSwatch.BorderBrush = selBrush;
                if (_customSwatch.Content is FontIcon fi) fi.Foreground = new SolidColorBrush(ContrastOn(c));
            }
        }
    }

    /// <summary>Black or white, whichever reads better on the given background color.</summary>
    private static Color ContrastOn(Color c)
    {
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return lum > 0.6 ? Color.FromArgb(255, 0, 0, 0) : Colors.White;
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
            var title = new TextBlock
            {
                Text = g.Name,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            content.Children.Add(title);
            if (g.Key == "minimize") _minimizeCardTitle = title;
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
                MinHeight = 132,
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
                MinItemHeight = 132,
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
        UpdateAppCompatibilityControls();
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
        AppsPane.Visibility = tag == "apps" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePane.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPane.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        AboutPane.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement active = tag switch
        {
            "snapping" => SnappingPane,
            "apps" => AppsPane,
            "appearance" => AppearancePane,
            "updates" => UpdatesPane,
            "about" => AboutPane,
            _ => GeneralPane,
        };
        AnimatePaneIn(active);
    }

    /// <summary>Windows 11 Settings-style page transition: the incoming pane slides up a few
    /// pixels and fades in. Runs on the composition (GPU) layer so it stays smooth.</summary>
    private void AnimatePaneIn(UIElement pane)
    {
        var visual = ElementCompositionPreview.GetElementVisual(pane);
        var comp = visual.Compositor;
        var ease = comp.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1.0f));

        var slide = comp.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new System.Numerics.Vector3(0f, 56f, 0f));
        slide.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(450);

        var fade = comp.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(450);

        visual.StartAnimation("Offset", slide);
        visual.StartAnimation("Opacity", fade);
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
            HeroUpdateTitle.Text = "Update available";
        }
        else
        {
            UpdateStatus.Text = "You're up to date";
            UpdateSub.Text = $"v{_updates.CurrentVersion} is the latest release.";
            HeroUpdateTitle.Text = "Up to date";
        }
        CheckBtn.IsEnabled = true;
    }

    // ---- Diagnostics ------------------------------------------------------

    private static string DiagnosticsPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Swoosh", "diagnostics.txt");

    /// <summary>Copy the touchpad/system diagnostics report (written by the running app at
    /// startup) to the clipboard, with brief confirmation on the button.</summary>
    private async void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        string report;
        try { report = System.IO.File.ReadAllText(DiagnosticsPath); }
        catch
        {
            report = "Swoosh diagnostics were not available. Make sure Swoosh is running, then try again.";
        }

        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(report);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch { /* clipboard unavailable */ }

        if (CopyDiagText != null && CopyDiagIcon != null)
        {
            CopyDiagText.Text = "Copied!";
            CopyDiagIcon.Glyph = "\uE73E"; // checkmark
            await Task.Delay(1500);
            CopyDiagText.Text = "Copy";
            CopyDiagIcon.Glyph = "\uE8C8"; // copy
        }
    }

    /// <summary>Ask the running tray app to show the gesture tutorial, via a shared named event.
    /// The tutorial window lives in the tray app (a separate process), so we signal it here.</summary>
    private void OnReplayTutorial(object sender, RoutedEventArgs e)
    {
        try
        {
            // Create-or-open the event; AutoReset so the listener consumes it once.
            using var signal = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, @"Local\Swoosh_Show_Tutorial_v1");
            signal.Set();
        }
        catch { /* tray app not running or signal unavailable */ }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser available */ }
    }

    // ---- Share ------------------------------------------------------------

    private const string ShareUrl = "https://github.com/bwya77/swoosh";
    private const string ShareMessage =
        "Swoosh brings macOS Swish-style touchpad window snapping to Windows. Free and open source: " + ShareUrl;

    /// <summary>Copy the repo link to the clipboard and briefly confirm on the button.</summary>
    private async void OnCopyShareLink(object sender, RoutedEventArgs e)
    {
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(ShareUrl);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch { /* clipboard unavailable */ }

        if (CopyLinkText != null && CopyLinkIcon != null)
        {
            CopyLinkText.Text = "Copied!";
            CopyLinkIcon.Glyph = "\uE73E"; // checkmark
            await Task.Delay(1500);
            CopyLinkText.Text = "Copy link";
            CopyLinkIcon.Glyph = "\uE8C8"; // copy
        }
    }

    /// <summary>Open the native Windows Share sheet so the link can go to Mail, Teams, etc.</summary>
    private void OnShareNative(object sender, RoutedEventArgs e)
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var interop = Windows.ApplicationModel.DataTransfer.DataTransferManager
                .As<IDataTransferManagerInterop>();
            var iid = _dtmIid;
            IntPtr result = interop.GetForWindow(hWnd, ref iid);
            var dtm = WinRT.MarshalInterface<Windows.ApplicationModel.DataTransfer.DataTransferManager>.FromAbi(result);

            dtm.DataRequested += (_, args) =>
            {
                var d = args.Request.Data;
                d.Properties.Title = "Swoosh";
                d.Properties.Description = "Swish-style touchpad window snapping for Windows";
                d.SetWebLink(new Uri(ShareUrl));
                d.SetText(ShareMessage);
            };
            interop.ShowShareUIForWindow(hWnd);
        }
        catch
        {
            // Share contract unavailable (rare): fall back to copying the link.
            OnCopyShareLink(sender, e);
        }
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [System.Runtime.InteropServices.InterfaceType(
        System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([System.Runtime.InteropServices.In] IntPtr appWindow,
            [System.Runtime.InteropServices.In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid _dtmIid =
        new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);
}
