using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Swoosh.Native;
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
    // Near-opaque dark backdrop so the (user-coloured) highlight fill always reads,
    // even with a grey accent over a light or busy wallpaper. A mostly-translucent
    // backdrop blended into bright desktops and washed the contrast out.
    private static readonly Brush ScreenBg = Freeze(new SolidColorBrush(Color.FromArgb(212, 18, 20, 26)));

    private static readonly Brush DefaultSolid = Freeze(new SolidColorBrush(Color.FromArgb(235, 10, 132, 255)));

    // Highlight brushes, recolored from settings (Windows accent or a custom color).
    private Brush _solid = DefaultSolid;
    private Brush _faint = Freeze(new SolidColorBrush(Color.FromArgb(70, 10, 132, 255)));

    // Whether the snap fill glides between zones (mirrors the window-move animation).
    private bool _animate = true;

    private static readonly Duration FillDuration = new(TimeSpan.FromMilliseconds(210));
    private static readonly IEasingFunction FillEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    // Desktop-strip "unfold" reveal: the extra squares slide out from behind the current one.
    private static readonly Duration RevealDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration RevealFadeDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly IEasingFunction RevealEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private Window? _win;
    private Canvas? _canvas;
    private DispatcherTimer? _hideTimer;

    // Persistent elements: single-monitor chip (snap mode).
    private Grid? _single;
    private Canvas? _singleInner;
    private Border? _singleFill;

    // Desktop mini-map strip: one square per virtual desktop, rebuilt when the
    // desktop count changes; only the fill brushes change per frame.
    private Grid? _strip;
    private readonly List<Border> _stripFills = new();
    private readonly List<Border> _stripScreens = new();
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

    // The monitor map is anchored to the cursor position captured when the gesture
    // begins, so tiny pointer jitter while the fingers rest does not make the (large)
    // HUD twitch frame to frame. Reset when the HUD hides.
    private bool _mapAnchored;
    private int _mapAnchorX, _mapAnchorY;

    // Pending placement, re-applied after a WM_DPICHANGED. When the window crosses a
    // DPI boundary, WPF raises that message asynchronously and resizes the HWND to keep
    // its WPF Width/Height; we re-pin our exact pixel placement and only then reveal it,
    // so it never appears at the wrong (tiny/huge) size mid-transition.
    private double _pendDesignW = SingleCanvasW, _pendDesignH = CanvasH, _pendBasePx = BaseHeightPx;
    private int _pendCurX, _pendCurY;
    private bool _pendHaveCursor;
    private uint _pendDpi = 96;

    /// <summary>Native handle of the HUD window once shown (else Zero). Used to carry
    /// the overlay across a virtual-desktop switch so it stays visible.</summary>
    public IntPtr Handle => _win == null ? IntPtr.Zero : new WindowInteropHelper(_win).Handle;

    /// <summary>Apply live appearance settings: whether the snap fill animates between
    /// zones, and the highlight color (the Windows accent color or a custom hex).</summary>
    public void ApplyAppearance(bool animate, bool useAccent, string customHex)
    {
        _animate = animate;

        Color c = AccentColors.Resolve(useAccent, customHex);
        _solid = Freeze(new SolidColorBrush(Color.FromArgb(235, c.R, c.G, c.B)));
        _faint = Freeze(new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B)));

        // Recolor anything currently on screen so the change is visible immediately.
        if (_singleFill is { Visibility: Visibility.Visible }) _singleFill.Background = _solid;
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
        _stripLefts.Clear();
        for (int i = 0; i < count; i++)
        {
            var (screen, _, fill) = BuildScreen(DeskW);
            double left = Margin + i * (DeskW + Gap);
            Canvas.SetLeft(screen, left);
            Canvas.SetTop(screen, Margin);
            host.Children.Add(screen);
            ResetFullFill(fill, DeskW);
            _stripFills.Add(fill);
            _stripScreens.Add(screen);
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

    /// <summary>Builds one monitor: rounded white-edged screen with a clipped inner
    /// canvas and a (hidden) blue fill rectangle. Returns the pieces for later updates.</summary>
    private static (Border screen, Canvas inner, Border fill) BuildScreen(double chipW) =>
        BuildScreenWH(chipW, ChipH);

    private static (Border screen, Canvas inner, Border fill) BuildScreenWH(double chipW, double chipH)
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
            BorderBrush = WhiteEdge,
            Background = ScreenBg,
            Child = inner,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 7,
                ShadowDepth = 1.5,
                Opacity = 0.45,
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
        _map.Visibility = Visibility.Visible;
        _canvas.Width = _mapDesignW;
        _canvas.Height = _mapDesignH;
    }

    /// <summary>Show the monitor-map plus. Only directions with an actual neighbor
    /// monitor are drawn; the current display is faintly tinted and the swiped-at
    /// target (when a monitor exists there) fills solid.</summary>
    public void ShowMonitorMap(bool up, bool down, bool left, bool right, MonitorDirection? target)
    {
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
            // Current monitor: always faintly tinted so the layout reads as "you are here".
            _mapCenterFill!.Background = _faint;
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

    private static void AnimateTo(UIElement el, DependencyProperty prop, double to)
    {
        var anim = new DoubleAnimation(to, FillDuration) { EasingFunction = FillEase };
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
    public void ShowDesktopStrip(int count, int currentIndex, DesktopDirection? lean, bool animateReveal = false)
    {
        if (count < 1) count = 1;
        EnsureWindow();
        if (_win == null) return;
        CancelHideTimer();
        EnsureStrip(count);
        SetStripMode();

        int leanIdx = lean switch
        {
            DesktopDirection.Right => currentIndex + 1,
            DesktopDirection.Left => currentIndex - 1,
            _ => -1,
        };

        string key = $"strip|{count}|{currentIndex}|{leanIdx}";
        if (key != _lastKey)
        {
            for (int i = 0; i < _stripFills.Count; i++)
            {
                var fill = _stripFills[i];
                if (i == currentIndex)
                {
                    fill.Background = _solid;
                    fill.Visibility = Visibility.Visible;
                }
                else if (i == leanIdx && leanIdx >= 0 && leanIdx < count)
                {
                    fill.Background = _faint;
                    fill.Visibility = Visibility.Visible;
                }
                else fill.Visibility = Visibility.Collapsed;
            }
            _lastKey = key;
        }
        Place(_stripDesignW, CanvasH);

        if (animateReveal) AnimateStripReveal(currentIndex);
    }

    /// <summary>Reveal the desktop strip by first showing only the current desktop square
    /// (centered, where the dwell chip was), then sliding every square out to its slot and
    /// fading the others in. Mirrors macOS-style "one then the rest unfold". When animation
    /// is disabled the squares simply appear in place.</summary>
    private void AnimateStripReveal(int currentIndex)
    {
        int n = _stripScreens.Count;
        if (n == 0) return;

        // Origin = strip centre, which is exactly where the single dwell chip sat, so the
        // squares appear to bloom out of that one chip with no positional jump.
        double originLeft = (_stripDesignW - DeskW) / 2.0;

        for (int i = 0; i < n; i++)
        {
            var screen = _stripScreens[i];
            double finalLeft = _stripLefts[i];
            bool isCurrent = i == currentIndex;

            // Clear any held animation from a previous reveal before re-pinning values.
            screen.BeginAnimation(Canvas.LeftProperty, null);
            screen.BeginAnimation(UIElement.OpacityProperty, null);

            if (!_animate || n == 1)
            {
                Canvas.SetLeft(screen, finalLeft);
                screen.Opacity = 1;
                continue;
            }

            Canvas.SetLeft(screen, originLeft);
            // The current desktop stays solid the whole time so you visibly "start" from it;
            // the rest fade in as they slide out.
            screen.Opacity = isCurrent ? 1 : 0;

            // Squares further from the current one start a touch later for a cascading feel.
            int distance = Math.Abs(i - currentIndex);
            var begin = TimeSpan.FromMilliseconds(22 * distance);

            var slide = new DoubleAnimation(originLeft, finalLeft, RevealDuration)
            {
                BeginTime = begin,
                EasingFunction = RevealEase,
            };
            screen.BeginAnimation(Canvas.LeftProperty, slide);

            if (!isCurrent)
            {
                var fade = new DoubleAnimation(0, 1, RevealFadeDuration)
                {
                    BeginTime = begin,
                    EasingFunction = RevealEase,
                };
                screen.BeginAnimation(UIElement.OpacityProperty, fade);
            }
        }
    }

    private void SetSingleMode()
    {
        if (_single == null || _canvas == null) return;
        _single.Visibility = Visibility.Visible;
        if (_strip != null) _strip.Visibility = Visibility.Collapsed;
        if (_map != null) _map.Visibility = Visibility.Collapsed;
        _canvas.Width = SingleCanvasW;
        _canvas.Height = CanvasH;
    }

    private void SetStripMode()
    {
        if (_single == null || _strip == null || _canvas == null) return;
        _single.Visibility = Visibility.Collapsed;
        _strip.Visibility = Visibility.Visible;
        if (_map != null) _map.Visibility = Visibility.Collapsed;
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

    private void Place(double designW, double designH, double basePx = BaseHeightPx, (int x, int y)? fixedCursor = null)
    {
        if (_win == null) return;

        _pendDesignW = designW;
        _pendDesignH = designH;
        _pendBasePx = basePx;
        if (fixedCursor is { } fc) { _pendCurX = fc.x; _pendCurY = fc.y; _pendHaveCursor = true; }
        else if (Win32.GetCursorPos(out var pt)) { _pendCurX = pt.X; _pendCurY = pt.Y; _pendHaveCursor = true; }
        else _pendHaveCursor = false;

        ApplyPlacement();

        // Reveal only once the window's WPF scale matches the target monitor. If the move
        // crossed a DPI boundary, WPF raises WM_DPICHANGED asynchronously and the hook
        // re-pins and reveals then. If no boundary was crossed, this reveals immediately.
        if (_win.Opacity == 0)
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
        int physH = (int)Math.Round(_pendBasePx * s);
        int physW = (int)Math.Round(physH * _pendDesignW / _pendDesignH);

        // DIP size is DPI-independent: this is the size WPF must preserve across monitors.
        _win.Width = physW / s;
        _win.Height = physH / s;

        int x = -10000, y = -10000;
        if (_pendHaveCursor)
        {
            x = _pendCurX - physW / 2;
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
        if (_win == null || _win.Opacity != 0) return;
        uint winDpi = (uint)Math.Round(VisualTreeHelper.GetDpi(_win).PixelsPerDip * 96);
        if (winDpi == _pendDpi) _win.Opacity = 1;
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
        // Reset the snap fill so the next gesture's first zone appears instantly
        // instead of gliding from wherever the previous gesture left it.
        ClearFillAnimations();
        if (_singleFill != null) _singleFill.Visibility = Visibility.Collapsed;
        // Hide by going fully transparent instead of _win.Hide(). Keeping the HWND shown
        // means the next gesture's cross-DPI move rescales an invisible window rather than
        // flashing during a Show().
        if (_win != null) _win.Opacity = 0;
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
