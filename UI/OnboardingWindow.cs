using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Rectangle = System.Windows.Shapes.Rectangle;
using Control = System.Windows.Controls.Control;

namespace Swoosh.UI;

/// <summary>
/// A short, visual first-run tutorial. Each step animates a two-finger swipe on a mini touchpad
/// (left) synced with the result on a mini screen (right): snapping, maximizing, and switching
/// virtual desktops. Branded surface that follows the OS light/dark
/// theme. Shown once on first launch and replayable from the tray menu.
/// </summary>
public sealed class OnboardingWindow : Window
{
    private enum Swipe { None, Left, Right, Up, Down, UpLeft }
    private enum Demo { Snap, Desktop, Resize, DownChooser, None }

    private sealed record Step(string Title, string Desc, Swipe Swipe, bool Hold, Demo Demo, Rect? Zone);

    private static readonly Rect RestRect = new(0.30, 0.26, 0.40, 0.48);

    private readonly Step[] _steps =
    {
        new("Welcome to Swoosh",
            "Hover your cursor over a window's titlebar, then swipe two fingers on your touchpad. Here, swiping right snaps the window to the right half. Lift to drop, or press Esc to cancel.",
            Swipe.Right, false, Demo.Snap, new Rect(0.5, 0, 0.5, 1)),
        new("Maximize and minimize",
            "Swipe up to maximize the window. Swipe down to minimize it.",
            Swipe.Up, false, Demo.Snap, new Rect(0, 0, 1, 1)),
        new("Snap to a corner",
            "Swipe diagonally to snap the window into that quarter of the screen.",
            Swipe.UpLeft, false, Demo.Snap, new Rect(0, 0, 0.5, 0.5)),
        new("Minimize or close",
            "Set Swipe down to Let me choose in Settings: swipe down and a picker drops out below the window. Lean left to minimize, or right for the red close button.",
            Swipe.Down, false, Demo.DownChooser, null),
        new("Resize with five fingers",
            "Put five fingers on the touchpad and spread them apart to grow the window, or pinch them together to shrink it.",
            Swipe.None, false, Demo.Resize, null),
        new("Switch virtual or physical desktops",
            "Hold two fingers still for a moment, then swipe to move the window to another virtual desktop or display. A strip shows where it will land. Swipe farther to jump several at once.",
            Swipe.Right, true, Demo.Desktop, null),
        new("You're all set",
            "Hold Shift while swiping for thirds. Open Settings any time from the Swoosh tray icon, where you can replay this tutorial from Show tutorial.",
            Swipe.None, false, Demo.None, null),
    };

    private int _index;
    private readonly Color _accent;
    private Palette _pal;

    // Demo surfaces.
    private const double PadW = 250, PadH = 158, ScrW = 250, ScrH = 158;
    private Canvas _padCanvas = null!, _scrCanvas = null!;
    private Ellipse _f1 = null!, _f2 = null!, _f1Glow = null!, _f2Glow = null!;
    private readonly Ellipse[] _five = new Ellipse[5];
    private readonly Ellipse[] _fiveGlow = new Ellipse[5];
    private Polyline _trail1 = null!, _trail2 = null!;
    private Border _window = null!;
    private TextBlock _titleText = null!, _descText = null!;
    private StackPanel _dots = null!;
    private Button _backBtn = null!, _nextBtn = null!;
    private Border _rootBorder = null!;
    private TextBlock _padCap = null!, _scrCap = null!, _arrow = null!;
    private Button _skip = null!;
    private Grid _demoRow = null!;
    private StackPanel _demoSurfaces = null!;
    private System.Windows.Shapes.Path _checkPath = null!;
    private Ellipse _checkRing = null!;
    private Image? _logoImg;
    private TextBlock _wordmark = null!;
    private readonly List<Action> _themeAppliers = new();

    private readonly DispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // When set, the animation uses this fixed time instead of the wall clock, so frames can be
    // rendered deterministically for exporting README GIFs.
    private double? _timeOverrideMs;
    private double Now() => _timeOverrideMs ?? _clock.Elapsed.TotalMilliseconds;

    private const double HoldRestMs = 600, SwipeMs = 1050, SettleMs = 750, GapMs = 450;

    public event Action? Completed;

    public OnboardingWindow(Color accent)
    {
        _accent = accent;
        _pal = Palette.For(SystemUsesLightTheme());

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "Welcome to Swoosh";
        Topmost = true;

        Content = BuildContent();
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Finish();
            else if (e.Key == System.Windows.Input.Key.Enter) Next();
        };

        RenderStep();

        // Follow OS light/dark changes live while the window is open.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Animate();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        };
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        Dispatcher.BeginInvoke(() =>
        {
            var pal = Palette.For(SystemUsesLightTheme());
            if (pal == _pal) return;
            _pal = pal;
            foreach (var apply in _themeAppliers) apply();
            RenderStep();
        });
    }

    /// <summary>Read the OS app theme (HKCU AppsUseLightTheme); default to dark on failure.</summary>
    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { }
        return false;
    }

    private readonly record struct Palette(
        Color Surface, Color SurfaceBorder, Color Text, Color SubText,
        Color ScreenTop, Color ScreenBottom, Color ScreenBorder, Color Guide, Color SecondaryBtn)
    {
        public static Palette For(bool light) => light
            ? new Palette(
                Surface: Color.FromRgb(0xF6, 0xF6, 0xF8),
                SurfaceBorder: Color.FromArgb(40, 0, 0, 0),
                Text: Color.FromRgb(0x1A, 0x1A, 0x1E),
                SubText: Color.FromArgb(190, 0, 0, 0),
                ScreenTop: Color.FromRgb(0xFF, 0xFF, 0xFF),
                ScreenBottom: Color.FromRgb(0xEC, 0xEC, 0xF0),
                ScreenBorder: Color.FromArgb(45, 0, 0, 0),
                Guide: Color.FromArgb(28, 0, 0, 0),
                SecondaryBtn: Color.FromArgb(16, 0, 0, 0))
            : new Palette(
                Surface: Color.FromRgb(0x1A, 0x1B, 0x20),
                SurfaceBorder: Color.FromArgb(60, 255, 255, 255),
                Text: Color.FromRgb(0xFF, 0xFF, 0xFF),
                SubText: Color.FromArgb(200, 255, 255, 255),
                ScreenTop: Color.FromRgb(0x24, 0x26, 0x2E),
                ScreenBottom: Color.FromRgb(0x16, 0x17, 0x1C),
                ScreenBorder: Color.FromArgb(55, 255, 255, 255),
                Guide: Color.FromArgb(20, 255, 255, 255),
                SecondaryBtn: Color.FromArgb(30, 255, 255, 255));
    }

    private static BitmapImage? LoadLogo()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("swoosh-256.png");
            if (s == null) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = s;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private UIElement BuildContent()
    {
        _themeAppliers.Clear();
        var stack = new StackPanel { Orientation = Orientation.Vertical };

        // ---- Header: logo + title ----
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var logo = LoadLogo();
        if (logo != null)
        {
            _logoImg = new Image { Source = logo, Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            header.Children.Add(_logoImg);
        }
        _wordmark = new TextBlock
        {
            Text = "Swoosh",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _themeAppliers.Add(() => _wordmark.Foreground = new SolidColorBrush(_pal.Text));
        header.Children.Add(_wordmark);
        stack.Children.Add(header);

        // ---- Demo: touchpad -> screen ----
        _padCanvas = new Canvas { Width = PadW, Height = PadH, ClipToBounds = true };
        _scrCanvas = new Canvas { Width = ScrW, Height = ScrH, ClipToBounds = true };
        BuildPad();
        BuildScreenWindow();
        BuildChooserDemo();

        var pad = WrapSurface(_padCanvas, "Your touchpad", out _padCap);
        var scr = WrapSurface(_scrCanvas, "Your screen", out _scrCap);

        _arrow = new TextBlock
        {
            Text = "\u2192",
            FontSize = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 18),
        };
        _themeAppliers.Add(() => _arrow.Foreground = new SolidColorBrush(Fade(_pal.SubText, 0.7)));

        _demoSurfaces = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _demoSurfaces.Children.Add(pad);
        _demoSurfaces.Children.Add(_arrow);
        _demoSurfaces.Children.Add(scr);

        // The demo row is a fixed-height container so every step (including the final summary,
        // which shows a checkmark instead of the pad/screen) keeps the window the same size.
        // Fixed height so every step (including the checkmark summary) keeps the window the same
        // size. PadH + 46 leaves room for the "Your touchpad" / "Your screen" captions below the
        // surfaces without clipping them.
        _demoRow = new Grid { Height = PadH + 46, HorizontalAlignment = HorizontalAlignment.Center };
        _demoRow.Children.Add(_demoSurfaces);
        _demoRow.Children.Add(BuildCheckmark());
        stack.Children.Add(_demoRow);

        // ---- Caption ----
        _titleText = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 6),
        };
        _themeAppliers.Add(() => _titleText.Foreground = new SolidColorBrush(_pal.Text));
        _descText = new TextBlock
        {
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 540,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
            MinHeight = 56,
        };
        _themeAppliers.Add(() => _descText.Foreground = new SolidColorBrush(_pal.SubText));
        stack.Children.Add(_titleText);
        stack.Children.Add(_descText);

        // ---- Footer: centered progress dots, then Back (left) / Next (right) below ----
        var footer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 16, 0, 0) };

        _dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
        };
        for (int i = 0; i < _steps.Length; i++)
            _dots.Children.Add(new Ellipse { Width = 8, Height = 8, Margin = new Thickness(3, 0, 3, 0) });

        var navRow = new Grid();
        navRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _backBtn = MakeButton("Back", accent: false);
        _backBtn.Click += (_, _) => Back();
        _backBtn.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(_backBtn, 0);

        _nextBtn = MakeButton("Next", accent: true);
        _nextBtn.Click += (_, _) => Next();
        _nextBtn.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_nextBtn, 2);

        navRow.Children.Add(_backBtn);
        navRow.Children.Add(_nextBtn);

        footer.Children.Add(_dots);
        footer.Children.Add(navRow);
        stack.Children.Add(footer);

        // ---- Skip ----
        // A Button (not a TextBlock) so it has proper click semantics and consumes its own
        // mouse-down, preventing the window's DragMove from swallowing the click.
        var skipBtn = new Button
        {
            Content = "Skip",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(8, 3, 8, 3),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = BuildButtonTemplate(),
        };
        skipBtn.Click += (_, _) => Finish();
        _skip = skipBtn;
        _themeAppliers.Add(() => _skip.Foreground = new SolidColorBrush(Fade(_pal.SubText, 0.7)));
        stack.Children.Add(_skip);

        _rootBorder = new Border
        {
            Width = 620,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(28, 24, 28, 22),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.40, Color = Colors.Black },
            Child = stack,
        };
        _themeAppliers.Add(() =>
        {
            _rootBorder.Background = new SolidColorBrush(_pal.Surface);
            _rootBorder.BorderBrush = new SolidColorBrush(_pal.SurfaceBorder);
        });

        ApplyTheme();

        // Transparent outer host with margin so the drop shadow isn't clipped to a square by the
        // (SizeToContent) window edge, which otherwise shows grey square corners past the radius.
        var host = new Grid { Background = Brushes.Transparent, Margin = new Thickness(34) };
        host.Children.Add(_rootBorder);
        return host;
    }

    private FrameworkElement WrapSurface(Canvas canvas, string label, out TextBlock caption)
    {
        var surface = new Border
        {
            Width = canvas.Width,
            Height = canvas.Height,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Child = canvas,
        };
        _themeAppliers.Add(() =>
        {
            surface.BorderBrush = new SolidColorBrush(_pal.ScreenBorder);
            surface.Background = new LinearGradientBrush(_pal.ScreenTop, _pal.ScreenBottom, 90);
        });
        var cap = new TextBlock
        {
            Text = label,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 9, 0, 2),
        };
        caption = cap;
        _themeAppliers.Add(() => cap.Foreground = new SolidColorBrush(_pal.Text));
        var sp = new StackPanel { Orientation = Orientation.Vertical };
        sp.Children.Add(surface);
        sp.Children.Add(cap);
        return sp;
    }

    /// <summary>The success checkmark shown on the final step: an accent ring with a check that
    /// draws on via a stroke-dash animation. Hidden until the summary step.</summary>
    private FrameworkElement BuildCheckmark()
    {
        const double size = 96;
        var host = new Grid
        {
            Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        _checkRing = new Ellipse
        {
            Width = size, Height = size,
            Fill = new SolidColorBrush(Color.FromArgb(36, _accent.R, _accent.G, _accent.B)),
            Stroke = new SolidColorBrush(_accent),
            StrokeThickness = 4,
        };
        host.Children.Add(_checkRing);

        // Checkmark geometry centered in the ring.
        var fig = new PathFigure { StartPoint = new Point(size * 0.30, size * 0.52) };
        fig.Segments.Add(new LineSegment(new Point(size * 0.44, size * 0.66), true));
        fig.Segments.Add(new LineSegment(new Point(size * 0.71, size * 0.36), true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);

        _checkPath = new System.Windows.Shapes.Path
        {
            Data = geo,
            Stroke = new SolidColorBrush(_accent),
            StrokeThickness = 6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        host.Children.Add(_checkPath);

        _checkHost = host;
        return host;
    }

    private Grid _checkHost = null!;

    private void PlayCheckmark()
    {
        // Pop the ring in, then draw the check stroke on.
        var pop = new System.Windows.Media.Animation.DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(260))
        { EasingFunction = new System.Windows.Media.Animation.BackEase { Amplitude = 0.6, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        var scale = new ScaleTransform(1, 1);
        _checkHost.RenderTransformOrigin = new Point(0.5, 0.5);
        _checkHost.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        var len = _checkPath.Data.GetFlattenedPathGeometry().GetArea(); // force realize
        _checkPath.StrokeDashArray = new DoubleCollection { 1, 1 };
        _checkPath.StrokeDashOffset = 1;
        // Use a geometry length approximation for the dash so it "draws on".
        _checkPath.StrokeDashArray = new DoubleCollection { 100, 100 };
        _checkPath.StrokeDashOffset = 100;
        var draw = new System.Windows.Media.Animation.DoubleAnimation(100, 0, TimeSpan.FromMilliseconds(420))
        {
            BeginTime = TimeSpan.FromMilliseconds(180),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        _checkPath.BeginAnimation(System.Windows.Shapes.Path.StrokeDashOffsetProperty, draw);
        _ = len;
    }

    private Line _guideV = null!, _guideH = null!;

    private void BuildPad()
    {
        _guideV = new Line { X1 = PadW / 2, Y1 = 12, X2 = PadW / 2, Y2 = PadH - 12, StrokeThickness = 1 };
        _guideH = new Line { X1 = 14, Y1 = PadH / 2, X2 = PadW - 14, Y2 = PadH / 2, StrokeThickness = 1 };
        _themeAppliers.Add(() =>
        {
            var g = new SolidColorBrush(_pal.Guide);
            _guideV.Stroke = g; _guideH.Stroke = g;
        });
        _padCanvas.Children.Add(_guideV);
        _padCanvas.Children.Add(_guideH);

        Brush accentLine = new SolidColorBrush(Color.FromArgb(120, _accent.R, _accent.G, _accent.B));
        _trail1 = NewTrail(accentLine);
        _trail2 = NewTrail(accentLine);
        _padCanvas.Children.Add(_trail1);
        _padCanvas.Children.Add(_trail2);

        _f1Glow = NewGlow();
        _f2Glow = NewGlow();
        _f1 = NewDot();
        _f2 = NewDot();
        _padCanvas.Children.Add(_f1Glow);
        _padCanvas.Children.Add(_f2Glow);
        _padCanvas.Children.Add(_f1);
        _padCanvas.Children.Add(_f2);

        // Five smaller dots for the resize demo (hidden until that step).
        for (int i = 0; i < 5; i++)
        {
            _fiveGlow[i] = NewGlow();
            _fiveGlow[i].Width = _fiveGlow[i].Height = 34;
            _fiveGlow[i].Visibility = Visibility.Collapsed;
            _five[i] = NewDot();
            _five[i].Width = _five[i].Height = 17;
            _five[i].Visibility = Visibility.Collapsed;
            _padCanvas.Children.Add(_fiveGlow[i]);
            _padCanvas.Children.Add(_five[i]);
        }
    }

    private Polyline NewTrail(Brush stroke) => new()
    {
        Stroke = stroke,
        StrokeThickness = 6,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };

    private Ellipse NewGlow() => new()
    {
        Width = 44, Height = 44, IsHitTestVisible = false,
        Fill = new RadialGradientBrush(
            Color.FromArgb(140, _accent.R, _accent.G, _accent.B),
            Color.FromArgb(0, _accent.R, _accent.G, _accent.B)),
    };

    private Ellipse NewDot() => new()
    {
        Width = 22, Height = 22,
        Fill = new SolidColorBrush(_accent),
        Stroke = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
        StrokeThickness = 2,
    };

    // Backdrop panels (desktops) drawn under the moving window for the Desktop demo.
    private readonly List<UIElement> _backdrop = new();
    private Border? _deskPage1, _deskPage2, _hudHighlight;
    private readonly List<Border> _hudPips = new();
    private double _slot;

    private void BuildScreenWindow()
    {
        _window = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(235, _accent.R, _accent.G, _accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderThickness = new Thickness(1.5),
            Child = new Border
            {
                Height = 8,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            },
        };
        _scrCanvas.Children.Add(_window);
    }

    // ---- Swipe-down chooser demo (greyed window + minimize/close option circles) -----------
    private Border _chooserSquare = null!;
    private Canvas _chooserCircleHost = null!;
    private TranslateTransform _chooserCircleT = null!;
    private Ellipse _chooserMin = null!, _chooserClose = null!;
    private TextBlock _chooserMinIcon = null!, _chooserCloseIcon = null!;

    private const double DcSqW = 74, DcSqH = 50, DcCircle = 42, DcCircleGap = 26;

    /// <summary>Builds the swipe-down chooser visualization shown on the "screen" surface for the
    /// minimize/close step: a greyed window square with two option circles (minimize left, red
    /// close right) that emerge downward as the fingers swipe down. Hidden on every other step.</summary>
    private void BuildChooserDemo()
    {
        double sqX = (ScrW - DcSqW) / 2.0, sqY = 26;
        _chooserSquare = new Border
        {
            Width = DcSqW,
            Height = DcSqH,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1.5),
            Visibility = Visibility.Collapsed,
            Child = new Border
            {
                Height = 7,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(6, 6, 0, 0),
            },
        };
        Canvas.SetLeft(_chooserSquare, sqX);
        Canvas.SetTop(_chooserSquare, sqY);
        _scrCanvas.Children.Add(_chooserSquare);

        _chooserCircleHost = new Canvas { Width = ScrW, Height = ScrH, Visibility = Visibility.Collapsed };
        _chooserCircleT = new TranslateTransform(0, 0);
        _chooserCircleHost.RenderTransform = _chooserCircleT;

        double rowW = 2 * DcCircle + DcCircleGap;
        double rowX = (ScrW - rowW) / 2.0;
        double rowY = sqY + DcSqH + 22;

        (_chooserMin, _chooserMinIcon) = BuildOptionCircle("\uE921", isClose: false);
        Canvas.SetLeft(_chooserMin, rowX);
        Canvas.SetTop(_chooserMin, rowY);
        Canvas.SetLeft(_chooserMinIcon, rowX);
        Canvas.SetTop(_chooserMinIcon, rowY);

        (_chooserClose, _chooserCloseIcon) = BuildOptionCircle("\uE8BB", isClose: true);
        double closeX = rowX + DcCircle + DcCircleGap;
        Canvas.SetLeft(_chooserClose, closeX);
        Canvas.SetTop(_chooserClose, rowY);
        Canvas.SetLeft(_chooserCloseIcon, closeX);
        Canvas.SetTop(_chooserCloseIcon, rowY);

        _chooserCircleHost.Children.Add(_chooserMin);
        _chooserCircleHost.Children.Add(_chooserMinIcon);
        _chooserCircleHost.Children.Add(_chooserClose);
        _chooserCircleHost.Children.Add(_chooserCloseIcon);
        _scrCanvas.Children.Add(_chooserCircleHost);

        _themeAppliers.Add(() =>
        {
            _chooserSquare.Background = new SolidColorBrush(Fade(_pal.SubText, 0.16));
            _chooserSquare.BorderBrush = new SolidColorBrush(Fade(_pal.SubText, 0.40));
            ((Border)_chooserSquare.Child).Background = new SolidColorBrush(Fade(_pal.SubText, 0.22));
            _chooserMin.Fill = new SolidColorBrush(Fade(_pal.SubText, 0.18));
            _chooserMin.Stroke = new SolidColorBrush(Fade(_pal.SubText, 0.45));
            _chooserMinIcon.Foreground = new SolidColorBrush(_pal.Text);
        });
    }

    private (Ellipse circle, TextBlock icon) BuildOptionCircle(string glyph, bool isClose)
    {
        var circle = new Ellipse
        {
            Width = DcCircle,
            Height = DcCircle,
            StrokeThickness = 2,
        };
        if (isClose)
        {
            circle.Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4A));
            circle.Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        }
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Width = DcCircle,
            Height = DcCircle,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(isClose ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(0, (DcCircle - 24) / 2.0, 0, 0),
        };
        return (circle, icon);
    }

    /// <summary>Rebuild the screen backdrop for the current step's demo. Snap shows one screen
    /// outline; Desktop builds a two-page filmstrip plus a desktop-switcher HUD strip. Called on
    /// step change and theme change.</summary>
    private void BuildBackdrop()
    {
        foreach (var b in _backdrop) _scrCanvas.Children.Remove(b);
        _backdrop.Clear();
        _deskPage1 = _deskPage2 = _hudHighlight = null;
        _hudPips.Clear();

        var step = _steps[_index];
        var panelFill = new SolidColorBrush(Fade(_pal.SubText, 0.10));
        var panelStroke = new SolidColorBrush(Fade(_pal.SubText, 0.30));

        switch (step.Demo)
        {
            case Demo.Snap:
            case Demo.Resize:
            case Demo.DownChooser:
                AddPanel(InnerRect(), panelFill, panelStroke, null);
                break;

            case Demo.Desktop:
                BuildDesktopFilmstrip(panelFill, panelStroke);
                break;

            case Demo.None:
                break;
        }
    }

    /// <summary>Two full-screen "desktop" pages laid side by side (a filmstrip wider than the
    /// viewport), plus a 3-tile desktop-switcher HUD strip near the bottom. Animate() pans the
    /// filmstrip and moves the HUD highlight to convey moving the window to the next desktop.</summary>
    private void BuildDesktopFilmstrip(Brush fill, Brush stroke)
    {
        var inner = InnerRect();
        _slot = inner.Width + 14; // distance the filmstrip pans for one desktop

        _deskPage1 = NewDesktopPage(inner, fill, stroke);
        _deskPage2 = NewDesktopPage(inner, fill, stroke);
        Canvas.SetTop(_deskPage1, inner.Y);
        Canvas.SetTop(_deskPage2, inner.Y);
        Canvas.SetLeft(_deskPage1, inner.X);
        Canvas.SetLeft(_deskPage2, inner.X + _slot);
        _scrCanvas.Children.Insert(0, _deskPage1);
        _scrCanvas.Children.Insert(1, _deskPage2);
        _backdrop.Add(_deskPage1);
        _backdrop.Add(_deskPage2);

        // HUD strip: three rounded desktop tiles centered near the bottom, with a highlight box
        // that slides from tile 1 to tile 2.
        const int tiles = 3;
        double tw = 30, th = 18, tgap = 7;
        double totalW = tiles * tw + (tiles - 1) * tgap;
        double startX = (ScrW - totalW) / 2;
        double y = ScrH - th - 9;

        var hudBack = new Border
        {
            Width = totalW + 12, Height = th + 10,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
        };
        Canvas.SetLeft(hudBack, startX - 6);
        Canvas.SetTop(hudBack, y - 5);
        _scrCanvas.Children.Add(hudBack);
        _backdrop.Add(hudBack);

        for (int i = 0; i < tiles; i++)
        {
            var tile = new Border
            {
                Width = tw, Height = th,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Canvas.SetLeft(tile, startX + i * (tw + tgap));
            Canvas.SetTop(tile, y);
            _scrCanvas.Children.Add(tile);
            _backdrop.Add(tile);
            _hudPips.Add(tile);
        }

        _hudHighlight = new Border
        {
            Width = tw, Height = th,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(70, _accent.R, _accent.G, _accent.B)),
            BorderBrush = new SolidColorBrush(_accent),
            BorderThickness = new Thickness(1.5),
        };
        Canvas.SetLeft(_hudHighlight, startX);
        Canvas.SetTop(_hudHighlight, y);
        _scrCanvas.Children.Add(_hudHighlight);
        _backdrop.Add(_hudHighlight);
        _hudStartX = startX;
        _hudStep = tw + tgap;
    }

    private double _hudStartX, _hudStep;

    private Border NewDesktopPage(Rect inner, Brush fill, Brush stroke) => new()
    {
        Width = inner.Width, Height = inner.Height,
        CornerRadius = new CornerRadius(6),
        Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(1),
    };

    private Rect InnerRect()
    {
        const double pad = 10;
        return new Rect(pad, pad, ScrW - pad * 2, ScrH - pad * 2);
    }

    private void AddPanel(Rect r, Brush fill, Brush stroke, string? label)
    {
        var rect = new Rectangle
        {
            Width = r.Width, Height = r.Height, RadiusX = 6, RadiusY = 6,
            Fill = fill, Stroke = stroke, StrokeThickness = 1,
        };
        Canvas.SetLeft(rect, r.X);
        Canvas.SetTop(rect, r.Y);
        _scrCanvas.Children.Insert(0, rect); // behind the window
        _backdrop.Add(rect);
        _ = label;
    }

    // Place the demo window given a fractional rect within a target area (canvas coords).
    private void PlaceWindowInArea(Rect area, Rect frac)
    {
        const double inset = 3;
        double x = area.X + frac.X * area.Width + inset;
        double y = area.Y + frac.Y * area.Height + inset;
        double w = Math.Max(0, frac.Width * area.Width - inset * 2);
        double h = Math.Max(0, frac.Height * area.Height - inset * 2);
        _window.Width = w;
        _window.Height = h;
        Canvas.SetLeft(_window, x);
        Canvas.SetTop(_window, y);
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Width + (b.Width - a.Width) * t,
        a.Height + (b.Height - a.Height) * t);

    private (double dx, double dy) SwipeVector(Swipe s) => s switch
    {
        Swipe.Left => (-1, 0),
        Swipe.Right => (1, 0),
        Swipe.Up => (0, -1),
        Swipe.Down => (0, 1),
        Swipe.UpLeft => (-0.72, -0.72),
        _ => (0, 0),
    };

    private void Animate()
    {
        var step = _steps[_index];

        if (step.Demo == Demo.Resize) { AnimateResize(); return; }

        // Hide the five-finger dots outside the resize step.
        for (int i = 0; i < 5; i++) { _five[i].Visibility = Visibility.Collapsed; _fiveGlow[i].Visibility = Visibility.Collapsed; }

        bool hasSwipe = step.Swipe != Swipe.None;

        double holdMs = step.Hold ? HoldRestMs : 0;
        double cycle = holdMs + SwipeMs + SettleMs + GapMs;
        double local = Now() % cycle;

        bool holding = step.Hold && local < holdMs;
        bool gap = local >= holdMs + SwipeMs + SettleMs;
        double swipeLocal = local - holdMs;
        double t;        // 0..1 eased progress
        double rawT;     // 0..1 linear (finger position)
        if (holding) { t = 0; rawT = 0; }
        else if (swipeLocal < SwipeMs) { rawT = Math.Clamp(swipeLocal / SwipeMs, 0, 1); t = EaseOut(rawT); }
        else if (!gap) { rawT = 1; t = 1; }
        else { rawT = 0; t = 0; }

        // ---- Touchpad fingers ----
        double cx = PadW / 2, cy = PadH / 2;
        double amp = 42;
        var (dx, dy) = SwipeVector(step.Swipe);
        double fx = cx + dx * amp * rawT;
        double fy = cy + dy * amp * rawT;
        double gapX = 15;

        double vis = hasSwipe && !gap ? 1 : 0;
        SetFinger(_f1, _f1Glow, fx - gapX, fy, vis);
        SetFinger(_f2, _f2Glow, fx + gapX, fy, vis);

        // Pulse the glow while holding to convey "hold still".
        if (holding)
        {
            double pulse = 0.6 + 0.4 * Math.Sin(Now() / 130.0);
            _f1Glow.Opacity = pulse; _f2Glow.Opacity = pulse;
        }

        if (hasSwipe && !gap && !holding)
        {
            ExtendTrail(_trail1, new Point(fx - gapX, fy), rawT);
            ExtendTrail(_trail2, new Point(fx + gapX, fy), rawT);
        }
        else { _trail1.Points.Clear(); _trail2.Points.Clear(); }

        // ---- Screen ----
        bool chooserStep = step.Demo == Demo.DownChooser;
        _chooserSquare.Visibility = chooserStep ? Visibility.Visible : Visibility.Collapsed;
        _chooserCircleHost.Visibility = chooserStep ? Visibility.Visible : Visibility.Collapsed;

        switch (step.Demo)
        {
            case Demo.Snap when step.Zone is { } zone:
            {
                _window.Visibility = Visibility.Visible;
                _window.Opacity = 1;
                var area = InnerRect();
                var rect = gap ? RestRect : Lerp(RestRect, zone, t);
                PlaceWindowInArea(area, rect);
                break;
            }
            case Demo.Desktop:
            {
                _window.Visibility = Visibility.Visible;
                _window.Opacity = 1;
                var inner = InnerRect();

                // Pan the desktop filmstrip left by one slot as the swipe progresses, so
                // desktop 1 slides out and desktop 2 slides in. The window stays centered
                // (it comes with you to the new desktop).
                double pan = gap ? 0 : t * _slot;
                if (_deskPage1 != null) Canvas.SetLeft(_deskPage1, inner.X - pan);
                if (_deskPage2 != null) Canvas.SetLeft(_deskPage2, inner.X + _slot - pan);

                // Window: keep it inside the panned current page so it visibly rides along, then
                // settles centered on desktop 2.
                var centered = new Rect(0.20, 0.24, 0.60, 0.42);
                PlaceWindowInArea(inner, centered);

                // HUD highlight slides from tile 1 to tile 2.
                if (_hudHighlight != null)
                    Canvas.SetLeft(_hudHighlight, _hudStartX + (gap ? 0 : t) * _hudStep);
                break;
            }
            case Demo.DownChooser:
            {
                // The window stays put (greyed square already shown). As the fingers swipe down,
                // the two option circles emerge downward and fade in, and the close option (right)
                // highlights to show a lean toward it. In the gap they retract.
                _window.Visibility = Visibility.Collapsed;

                double emerge = gap ? 0 : rawT;       // 0..1 reveal
                _chooserCircleT.Y = -(DcCircle * 0.7) * (1 - emerge);
                _chooserCircleHost.Opacity = gap ? 0 : EaseOut(Math.Clamp(rawT * 1.4, 0, 1));

                // Lean toward close once the swipe is past the midpoint: brighten close, dim minimize.
                bool closeLean = !gap && rawT > 0.5;
                _chooserClose.Opacity = closeLean ? 1.0 : 0.55;
                _chooserCloseIcon.Opacity = closeLean ? 1.0 : 0.55;
                _chooserMin.Opacity = closeLean ? 0.5 : 1.0;
                _chooserMinIcon.Opacity = closeLean ? 0.5 : 1.0;

                // The window dims further as the (highlighted) close action is about to commit.
                _chooserSquare.Opacity = closeLean ? 0.5 : 1.0;
                break;
            }
            default:
                _window.Visibility = Visibility.Collapsed;
                break;
        }
    }

    /// <summary>Five-finger resize demo: five dots fan out from the centroid (window grows) then
    /// draw back in (window shrinks), on a smooth sine loop, synced with the window scaling.</summary>
    private void AnimateResize()
    {
        // Hide the two-finger dots and trails.
        _f1.Opacity = _f2.Opacity = _f1Glow.Opacity = _f2Glow.Opacity = 0;
        _trail1.Points.Clear(); _trail2.Points.Clear();

        // s: 0 = pinched in (small), 1 = spread out (large), oscillating.
        double phase = Now() / 1500.0; // ~1.5s per half
        double s = 0.5 - 0.5 * Math.Cos(phase * Math.PI); // smooth 0..1..0

        double cx = PadW / 2, cy = PadH / 2;
        double radius = 16 + 40 * s; // fingers spread from 16px to 56px from centroid
        // Five dots like a hand fanned across the top: angles spanning ~200 degrees.
        for (int i = 0; i < 5; i++)
        {
            _five[i].Visibility = Visibility.Visible;
            _fiveGlow[i].Visibility = Visibility.Visible;
            double ang = (-200.0 + i * (220.0 / 4)) * Math.PI / 180.0; // -200..20 deg
            double x = cx + Math.Cos(ang) * radius;
            double y = cy + Math.Sin(ang) * radius * 0.78 + 8; // slightly squashed, nudged down
            SetFinger(_five[i], _fiveGlow[i], x, y, 1);
        }

        // Window grows/shrinks centered on the single screen.
        _window.Visibility = Visibility.Visible;
        _window.Opacity = 1;
        var area = InnerRect();
        double w = 0.30 + 0.50 * s;   // 30% -> 80% width
        double h = 0.28 + 0.46 * s;   // 28% -> 74% height
        var rect = new Rect((1 - w) / 2, (1 - h) / 2, w, h);
        PlaceWindowInArea(area, rect);
    }

    // ---- GIF export support -----------------------------------------------
    // Reused by the --export-tutorial command line path to render deterministic frames of each
    // demo for the README, without screen-capture artifacts.

    internal int StepCount => _steps.Length;
    internal bool StepHasDemo(int i) => _steps[i].Demo != Demo.None;

    /// <summary>A filename-safe key for a step's GIF (for example "snap-right").</summary>
    internal string StepKey(int i)
    {
        var s = _steps[i];
        return s.Demo switch
        {
            Demo.Resize => "resize",
            Demo.Desktop => "desktops",
            Demo.DownChooser => "swipe-down",
            Demo.Snap => s.Swipe switch
            {
                Swipe.Right => "snap-right",
                Swipe.Up => "maximize",
                Swipe.UpLeft => "snap-corner",
                _ => $"snap-{i}",
            },
            _ => $"step-{i}",
        };
    }

    /// <summary>One full animation loop duration (ms) for the given step.</summary>
    internal double StepCycleMs(int i)
    {
        var s = _steps[i];
        if (s.Demo == Demo.Resize) return 3000; // s goes 0..1..0 over phase 0..2 (1500ms each)
        double holdMs = s.Hold ? HoldRestMs : 0;
        return holdMs + SwipeMs + SettleMs + GapMs;
    }

    /// <summary>Switch to a step and build its backdrop, ready for frame rendering. Stops the
    /// live timer so only explicit RenderFrameAt calls drive the animation. Returns the surfaces
    /// panel (touchpad + screen with their captions) so exported frames include the labels.</summary>
    internal FrameworkElement PrepareExport(int i)
    {
        _timer.Stop();
        _index = i;
        RenderStep();
        return _demoRow;
    }

    /// <summary>Force the dark palette for deterministic GIF export and stop following the OS
    /// theme, so exported frames look the same regardless of the current system setting.</summary>
    internal void ForceDarkForExport()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _pal = Palette.For(false);
        foreach (var apply in _themeAppliers) apply();
    }

    /// <summary>Render the demo at a fixed time within the loop.</summary>
    internal void RenderFrameAt(double ms)
    {
        _timeOverrideMs = ms;
        Animate();
    }

    private void SetFinger(Ellipse dot, Ellipse glow, double x, double y, double opacity)
    {
        dot.Opacity = opacity;
        glow.Opacity = opacity;
        Canvas.SetLeft(dot, x - dot.Width / 2);
        Canvas.SetTop(dot, y - dot.Height / 2);
        Canvas.SetLeft(glow, x - glow.Width / 2);
        Canvas.SetTop(glow, y - glow.Height / 2);
    }

    private void ExtendTrail(Polyline trail, Point p, double rawT)
    {
        if (rawT <= 0.02) trail.Points.Clear();
        if (trail.Points.Count == 0 || (trail.Points[^1] - p).Length >= 3)
            trail.Points.Add(p);
        while (trail.Points.Count > 24) trail.Points.RemoveAt(0);
    }

    private void RenderStep()
    {
        var step = _steps[_index];
        _titleText.Text = step.Title;
        _descText.Text = step.Desc;
        _padCap.Text = step.Hold ? "Your touchpad (hold, then swipe)"
            : step.Demo == Demo.Resize ? "Your touchpad (five fingers)"
            : "Your touchpad";

        // The closing step shows a success checkmark instead of the pad/screen demo, but the
        // demo row keeps its height so the window stays the same size on every step.
        bool summary = step.Demo == Demo.None;
        _demoSurfaces.Visibility = summary ? Visibility.Collapsed : Visibility.Visible;
        _checkHost.Visibility = summary ? Visibility.Visible : Visibility.Collapsed;
        if (summary) PlayCheckmark();

        // The swipe-down chooser visuals belong only to their step; hide them on every other step
        // (the resize step returns early from Animate, so it can't clear them itself).
        bool chooserStep = step.Demo == Demo.DownChooser;
        _chooserSquare.Visibility = chooserStep ? Visibility.Visible : Visibility.Collapsed;
        _chooserCircleHost.Visibility = chooserStep ? Visibility.Visible : Visibility.Collapsed;
        if (chooserStep)
        {
            // Start from the fully-tucked, invisible state so the first painted frame shows the
            // circles emerging from scratch rather than already present.
            _chooserCircleHost.Opacity = 0;
            _chooserCircleT.Y = -(DcCircle * 0.7);
            _chooserSquare.Opacity = 1;
        }

        for (int i = 0; i < _dots.Children.Count; i++)
            ((Ellipse)_dots.Children[i]).Fill = i == _index
                ? new SolidColorBrush(_accent)
                : new SolidColorBrush(Fade(_pal.SubText, 0.30));

        _backBtn.Visibility = _index == 0 ? Visibility.Hidden : Visibility.Visible;
        _nextBtn.Content = _index == _steps.Length - 1 ? "Get started" : "Next";

        BuildBackdrop();

        _clock.Restart();
        _trail1.Points.Clear();
        _trail2.Points.Clear();
    }

    private void Back() { if (_index > 0) { _index--; RenderStep(); } }

    private void Next()
    {
        if (_index >= _steps.Length - 1) { Finish(); return; }
        _index++;
        RenderStep();
    }

    private bool _finished;
    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        Completed?.Invoke();
        Close();
    }

    private static Color Fade(Color c, double mul) =>
        Color.FromArgb((byte)(c.A * Math.Clamp(mul, 0, 1)), c.R, c.G, c.B);

    private void ApplyTheme()
    {
        foreach (var apply in _themeAppliers) apply();
        // Secondary button styling depends on palette; refresh both buttons.
        StyleSecondaryButton(_backBtn);
    }

    // ---- Buttons ----

    private Button MakeButton(string text, bool accent)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(18, 7, 18, 8),
            MinWidth = 92,
            FontSize = 13.5,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = BuildButtonTemplate(),
        };
        if (accent)
        {
            btn.Foreground = Brushes.White;
            btn.BorderThickness = new Thickness(0);
            btn.BorderBrush = Brushes.Transparent;
            btn.Background = new SolidColorBrush(_accent);
        }
        else
        {
            _themeAppliers.Add(() => StyleSecondaryButton(btn));
        }
        return btn;
    }

    private void StyleSecondaryButton(Button? btn)
    {
        if (btn == null) return;
        btn.Foreground = new SolidColorBrush(_pal.Text);
        btn.BorderThickness = new Thickness(1);
        btn.BorderBrush = new SolidColorBrush(_pal.SurfaceBorder);
        btn.Background = new SolidColorBrush(_pal.SecondaryBtn);
    }

    private static ControlTemplate BuildButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        t.VisualTree = border;
        return t;
    }
}
