using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Swoosh.Input;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace Swoosh.UI;

/// <summary>
/// A presentation-quality touchpad visualizer for screen recordings and demos. Unlike the
/// debug overlay, this renders a clean branded touchpad surface with glowing finger dots,
/// fading "comet" trails, a finger-count pill, and a live caption describing the active
/// gesture (e.g. "Snap left"). It is draggable, always-on-top, and never steals focus, so it
/// can sit in a corner of the screen while you record with OBS.
/// </summary>
public sealed class DemoOverlay
{
    private Window? _win;
    private Canvas? _canvas;
    private Border? _pill;
    private TextBlock? _pillText;
    private TextBlock? _caption;
    private Border? _captionHost;

    // Logical size of the touchpad surface. 1.6:1 roughly matches a real Precision Touchpad.
    private const double PadW = 440, PadH = 275;
    private const long TrailMs = 650;
    private const int MaxTrailPoints = 16;   // cap history so 5 fingers stay cheap to draw
    private const double MinTrailStep = 5.0;  // decimate: skip samples closer than this (px)

    private Color _accent = Color.FromRgb(0x0A, 0x84, 0xFF);
    private long _captionUntil;

    // Cached, frozen brushes so the hot redraw path allocates as little as possible. The
    // accent-derived ones are rebuilt only when the accent changes.
    private Brush _guideBrush = Frozen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)));
    private Brush _glowBrush = Brushes.Transparent;
    private Brush _coreFill = Brushes.Transparent;
    private Brush _coreStroke = Frozen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)));

    // Per-contact motion history (point + timestamp) used to draw fading trails.
    private readonly Dictionary<int, LinkedList<(Point p, long t)>> _trails = new();
    // Fingers currently down, redrawn each tick so trails keep fading after input stops.
    private readonly List<Point> _down = new();
    private DispatcherTimer? _timer;

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    private void RebuildAccentBrushes()
    {
        var glow = new RadialGradientBrush(
            Color.FromArgb(150, _accent.R, _accent.G, _accent.B),
            Color.FromArgb(0, _accent.R, _accent.G, _accent.B));
        glow.Freeze();
        _glowBrush = glow;
        _coreFill = Frozen(new SolidColorBrush(_accent));
    }

    public bool IsVisible => _win is { IsVisible: true };

    public void SetAccent(Color c) { _accent = c; RebuildAccentBrushes(); }

    private void Ensure()
    {
        if (_win != null) return;

        if (_glowBrush == Brushes.Transparent) RebuildAccentBrushes();

        _canvas = new Canvas { Width = PadW, Height = PadH, ClipToBounds = true };

        // Subtle centre guide lines so motion reads against the surface.
        var guideBrush = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
        _canvas.Children.Add(new Line { X1 = PadW / 2, Y1 = 10, X2 = PadW / 2, Y2 = PadH - 10, Stroke = guideBrush, StrokeThickness = 1 });
        _canvas.Children.Add(new Line { X1 = 14, Y1 = PadH / 2, X2 = PadW - 14, Y2 = PadH / 2, Stroke = guideBrush, StrokeThickness = 1 });

        var pad = new Border
        {
            Width = PadW,
            Height = PadH,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            Background = new LinearGradientBrush(
                Color.FromRgb(0x1B, 0x1D, 0x24), Color.FromRgb(0x10, 0x11, 0x16), 90),
            Child = _canvas,
        };

        // ---- Header: wordmark + finger-count pill ----
        var dot = new Ellipse
        {
            Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(_accent),
        };
        var word = new TextBlock
        {
            Text = "Swoosh",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(dot);
        brand.Children.Add(word);

        _pillText = new TextBlock
        {
            Text = "0 fingers",
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 4),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _pillText,
        };

        var header = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(brand, 0);
        Grid.SetColumn(_pill, 1);
        header.Children.Add(brand);
        header.Children.Add(_pill);

        // ---- Caption (active gesture) ----
        _caption = new TextBlock
        {
            Text = "",
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _captionHost = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 5, 12, 6),
            Background = new SolidColorBrush(Color.FromArgb(235, _accent.R, _accent.G, _accent.B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _caption,
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(header);
        stack.Children.Add(pad);
        stack.Children.Add(_captionHost);

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 22, 23, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 12, 14, 14),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black },
            Child = stack,
        };

        _win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false,
            Content = root,
            Title = "Swoosh Touchpad",
        };
        // Drag anywhere to reposition for framing the shot.
        _win.MouseLeftButtonDown += (_, _) => { try { _win!.DragMove(); } catch { } };

        // ~60fps redraw so trails keep fading smoothly even after the fingers lift and
        // touch frames stop arriving. The tick is a no-op (and stops itself) once there is
        // nothing left to animate.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => DrawScene();

        var work = SystemParameters.WorkArea;
        _win.Left = work.Right - PadW - 90;
        _win.Top = work.Bottom - PadH - 150;
    }

    public void Toggle()
    {
        Ensure();
        if (_win!.IsVisible) { _win.Hide(); _trails.Clear(); _down.Clear(); _timer?.Stop(); }
        else _win.Show();
    }

    public void SetVisible(bool visible)
    {
        if (!visible && _win == null) return;
        Ensure();
        if (visible) _win!.Show();
        else { _win!.Hide(); _trails.Clear(); _down.Clear(); _timer?.Stop(); }
    }

    /// <summary>Show a short caption describing the active gesture. Pass null or empty to clear.
    /// The caption auto-hides shortly after the last update so it doesn't linger on video.</summary>
    public void SetCaption(string? text)
    {
        if (_captionHost == null || _caption == null) return;
        if (string.IsNullOrEmpty(text))
        {
            _captionHost.Visibility = Visibility.Collapsed;
            _captionUntil = 0;
            return;
        }
        _caption.Text = text;
        _captionHost.Background = new SolidColorBrush(Color.FromArgb(235, _accent.R, _accent.G, _accent.B));
        _captionHost.Visibility = Visibility.Visible;
        _captionUntil = Environment.TickCount64 + 900;
    }

    public void Render(TouchFrame frame)
    {
        if (_win is not { IsVisible: true } || _canvas == null) return;

        long now = Environment.TickCount64;
        int down = frame.DownCount;

        if (_pillText != null)
            _pillText.Text = down == 1 ? "1 finger" : $"{down} fingers";
        if (_pill != null)
        {
            Color pillBg = down switch
            {
                2 => Color.FromArgb(235, _accent.R, _accent.G, _accent.B),
                >= 3 => Color.FromArgb(235, 255, 165, 60),
                _ => Color.FromArgb(40, 255, 255, 255),
            };
            _pill.Background = new SolidColorBrush(pillBg);
        }

        // Record motion history and the current finger positions for this frame.
        _down.Clear();
        foreach (var c in frame.Contacts)
        {
            if (!c.TipDown) continue;
            var p = new Point(c.X * PadW, c.Y * PadH);
            _down.Add(p);
            if (!_trails.TryGetValue(c.Id, out var hist))
            {
                hist = new LinkedList<(Point, long)>();
                _trails[c.Id] = hist;
            }
            // Decimate: only add a node once the finger has moved a little, so a resting or
            // slow finger doesn't pack the list with near-identical points (cheaper to draw).
            if (hist.Last is { } last)
            {
                double dx = p.X - last.Value.p.X, dy = p.Y - last.Value.p.Y;
                if (dx * dx + dy * dy < MinTrailStep * MinTrailStep)
                {
                    // Refresh the timestamp of the tip so the trail doesn't start fading while
                    // the finger is held still, but don't grow the list.
                    last.Value = (last.Value.p, now);
                    continue;
                }
            }
            hist.AddLast((p, now));
            while (hist.Count > MaxTrailPoints) hist.RemoveFirst();
        }

        // Keep the per-frame redraw timer running while there is anything to animate.
        if (_timer is { IsEnabled: false }) _timer.Start();

        DrawScene();
    }

    /// <summary>Age out old trail points and repaint the surface. Called both on each touch
    /// frame and on the redraw timer, so trails keep fading after the fingers lift.</summary>
    private void DrawScene()
    {
        if (_win is not { IsVisible: true } || _canvas == null) return;
        long now = Environment.TickCount64;

        // Auto-clear a stale caption once the gesture has been idle briefly.
        if (_captionUntil != 0 && now > _captionUntil && _captionHost != null)
        {
            _captionHost.Visibility = Visibility.Collapsed;
            _captionUntil = 0;
        }

        // Age out old trail points; drop empty trails (handles lifted fingers fading out).
        foreach (var id in _trails.Keys.ToList())
        {
            var hist = _trails[id];
            while (hist.First != null && now - hist.First.Value.t > TrailMs)
                hist.RemoveFirst();
            if (hist.Count == 0) _trails.Remove(id);
        }

        // ---- Redraw ----
        _canvas.Children.Clear();

        _canvas.Children.Add(new Line { X1 = PadW / 2, Y1 = 10, X2 = PadW / 2, Y2 = PadH - 10, Stroke = _guideBrush, StrokeThickness = 1 });
        _canvas.Children.Add(new Line { X1 = 14, Y1 = PadH / 2, X2 = PadW - 14, Y2 = PadH / 2, Stroke = _guideBrush, StrokeThickness = 1 });

        // Trails (under the dots). Draw each as a series of segments whose opacity and width
        // taper with age, so the tail fades out smoothly behind the moving finger.
        foreach (var hist in _trails.Values)
        {
            if (hist.Count < 2) continue;
            var pts = hist.ToArray();
            for (int i = 1; i < pts.Length; i++)
            {
                // position 0 = oldest sample (tail), 1 = newest (fingertip).
                double pos = (double)i / (pts.Length - 1);
                // overall fade-out of the whole trail based on the newest sample's age, so a
                // resting or lifted finger's trail dissolves smoothly over TrailMs.
                double life = 1.0 - Math.Clamp((now - pts[i].t) / (double)TrailMs, 0, 1);
                double k = pos * life; // brightest/thickest near the finger, faded at the tail
                byte a = (byte)(150 * k);
                if (a <= 4) continue;
                var brush = new SolidColorBrush(Color.FromArgb(a, _accent.R, _accent.G, _accent.B));
                brush.Freeze();
                var seg = new Line
                {
                    X1 = pts[i - 1].p.X, Y1 = pts[i - 1].p.Y,
                    X2 = pts[i].p.X, Y2 = pts[i].p.Y,
                    Stroke = brush,
                    StrokeThickness = 2 + 6 * k,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                _canvas.Children.Add(seg);
            }
        }

        // Finger dots (only for fingers currently down).
        foreach (var p in _down)
        {
            double cx = p.X, cy = p.Y;

            var glow = new Ellipse
            {
                Width = 56, Height = 56,
                Fill = _glowBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(glow, cx - 28);
            Canvas.SetTop(glow, cy - 28);
            _canvas.Children.Add(glow);

            var core = new Ellipse
            {
                Width = 26, Height = 26,
                Fill = _coreFill,
                Stroke = _coreStroke,
                StrokeThickness = 2,
            };
            Canvas.SetLeft(core, cx - 13);
            Canvas.SetTop(core, cy - 13);
            _canvas.Children.Add(core);
        }

        // Nothing left to animate: stop ticking until the next touch frame.
        if (_timer != null && _trails.Count == 0 && _down.Count == 0)
            _timer.Stop();
    }

    public void Close() { _timer?.Stop(); _win?.Close(); _win = null; _trails.Clear(); _down.Clear(); }
}
