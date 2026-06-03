using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
    private const double TwoChipW = 80;
    private const double TwoCanvasW = 2 * TwoChipW + Gap + 2 * Margin; // 174
    private const double CanvasH = ChipH + 2 * Margin;                 // 66

    private const double BaseHeightPx = 46; // physical chip-window height at 96 DPI

    private static readonly Brush WhiteEdge = Freeze(new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)));
    private static readonly Brush ScreenBg = Freeze(new SolidColorBrush(Color.FromArgb(96, 22, 24, 30)));
    private static readonly Brush BlueSolid = Freeze(new SolidColorBrush(Color.FromArgb(235, 10, 132, 255)));
    private static readonly Brush BlueFaint = Freeze(new SolidColorBrush(Color.FromArgb(70, 10, 132, 255)));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private Window? _win;
    private Canvas? _canvas;
    private DispatcherTimer? _hideTimer;

    // Persistent elements: single-monitor chip.
    private Grid? _single;
    private Canvas? _singleInner;
    private Border? _singleFill;

    // Persistent elements: two-desktop chips.
    private Grid? _two;
    private Border? _twoLeftFill;
    private Border? _twoRightFill;

    private bool _isTwo;
    private string _lastKey = "";
    private (int x, int y, int w, int h) _lastPlace = (-99999, 0, 0, 0);

    private void EnsureWindow()
    {
        if (_win != null) return;

        _canvas = new Canvas { Width = SingleCanvasW, Height = CanvasH };
        _single = BuildSingle();
        _two = BuildTwo();
        _canvas.Children.Add(_single);
        _canvas.Children.Add(_two);

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
        };
        _win.Show();
        _win.Hide();
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

    private Grid BuildTwo()
    {
        var root = new Grid { Width = TwoCanvasW, Height = CanvasH, Visibility = Visibility.Collapsed };
        var host = new Canvas { Width = TwoCanvasW, Height = CanvasH };

        var (lScreen, _, lFill) = BuildScreen(TwoChipW);
        Canvas.SetLeft(lScreen, Margin);
        Canvas.SetTop(lScreen, Margin);

        var (rScreen, _, rFill) = BuildScreen(TwoChipW);
        Canvas.SetLeft(rScreen, Margin + TwoChipW + Gap);
        Canvas.SetTop(rScreen, Margin);

        host.Children.Add(lScreen);
        host.Children.Add(rScreen);
        root.Children.Add(host);

        _twoLeftFill = lFill;
        _twoRightFill = rFill;
        // For desktop chips the fill always covers the whole inner area.
        ResetFullFill(lFill, TwoChipW);
        ResetFullFill(rFill, TwoChipW);
        return root;
    }

    /// <summary>Builds one monitor: rounded white-edged screen with a clipped inner
    /// canvas and a (hidden) blue fill rectangle. Returns the pieces for later updates.</summary>
    private static (Border screen, Canvas inner, Border fill) BuildScreen(double chipW)
    {
        double innerW = chipW - 2 * Stroke;
        double innerH = ChipH - 2 * Stroke;
        double innerCorner = Math.Max(0, Corner - Stroke);

        var fill = new Border
        {
            Background = BlueSolid,
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
            Height = ChipH,
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

    private static void ResetFullFill(Border fill, double chipW)
    {
        double innerW = chipW - 2 * Stroke;
        double innerH = ChipH - 2 * Stroke;
        fill.Width = innerW;
        fill.Height = innerH;
        Canvas.SetLeft(fill, 0);
        Canvas.SetTop(fill, 0);
    }

    // -------------------------------------------------------------------------

    /// <summary>Show the single-monitor chip with the given snap zone highlighted.</summary>
    public void ShowSnap(SnapZone zone, double progress)
    {
        EnsureWindow();
        if (_win == null) return;
        CancelHideTimer();
        SetMode(false);

        string key = $"s|{zone}";
        if (key != _lastKey)
        {
            UpdateSingleFill(zone);
            _lastKey = key;
        }
        Place(SingleCanvasW, CanvasH);
    }

    private void UpdateSingleFill(SnapZone zone)
    {
        if (_singleFill == null) return;
        var frac = ZoneFraction(zone);
        if (frac == null) { _singleFill.Visibility = Visibility.Collapsed; return; }

        double innerW = SingleChipW - 2 * Stroke;
        double innerH = ChipH - 2 * Stroke;
        var (x0, y0, x1, y1) = frac.Value;

        _singleFill.Width = Math.Max(0, (x1 - x0) * innerW);
        _singleFill.Height = Math.Max(0, (y1 - y0) * innerH);
        _singleFill.CornerRadius = new CornerRadius(zone == SnapZone.Center ? 4 : 0);
        _singleFill.Background = BlueSolid;
        Canvas.SetLeft(_singleFill, x0 * innerW);
        Canvas.SetTop(_singleFill, y0 * innerH);
        _singleFill.Visibility = Visibility.Visible;
    }

    /// <summary>Show the two-desktop chip. <paramref name="confirmed"/> fills the target
    /// solid blue (the move happened) and auto-hides shortly after; otherwise the aimed
    /// side gets a faint tint as a hover hint.</summary>
    public void ShowDesktops(DesktopDirection? target, bool confirmed)
    {
        EnsureWindow();
        if (_win == null) return;
        CancelHideTimer();
        SetMode(true);

        string key = $"d|{target}|{confirmed}";
        if (key != _lastKey)
        {
            ApplyDesktopFill(_twoLeftFill, target == DesktopDirection.Left, confirmed);
            ApplyDesktopFill(_twoRightFill, target == DesktopDirection.Right, confirmed);
            _lastKey = key;
        }
        Place(TwoCanvasW, CanvasH);

        if (confirmed) StartHideTimer(520);
    }

    private static void ApplyDesktopFill(Border? fill, bool isTarget, bool confirmed)
    {
        if (fill == null) return;
        if (!isTarget) { fill.Visibility = Visibility.Collapsed; return; }
        fill.Background = confirmed ? BlueSolid : BlueFaint;
        fill.Visibility = Visibility.Visible;
    }

    private void SetMode(bool two)
    {
        if (_single == null || _two == null || _canvas == null) return;
        if (_isTwo == two && _canvas.Children.Count > 0) { /* still ensure sizes below */ }
        _isTwo = two;
        _single.Visibility = two ? Visibility.Collapsed : Visibility.Visible;
        _two.Visibility = two ? Visibility.Visible : Visibility.Collapsed;
        _canvas.Width = two ? TwoCanvasW : SingleCanvasW;
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
        SnapZone.Minimize => (0.32, 0.82, 0.68, 1.0),
        _ => null,
    };

    private void Place(double designW, double designH)
    {
        if (_win == null) return;

        uint dpi = Win32.GetDpiForCursor();
        double s = dpi / 96.0;
        int physH = (int)Math.Round(BaseHeightPx * s);
        int physW = (int)Math.Round(physH * designW / designH);

        int x = -10000, y = -10000;
        if (Win32.GetCursorPos(out var pt))
        {
            x = pt.X - physW / 2;
            y = pt.Y + (int)Math.Round(14 * s);
        }

        if (!_win.IsVisible) _win.Show();

        var place = (x, y, physW, physH);
        if (place == _lastPlace) return;
        _lastPlace = place;

        IntPtr h = new WindowInteropHelper(_win).Handle;
        Win32.SetWindowPos(h, Win32.HWND_TOPMOST, x, y, physW, physH, Win32.SWP_NOACTIVATE);
    }

    public void Hide()
    {
        CancelHideTimer();
        _lastKey = "";
        _lastPlace = (-99999, 0, 0, 0);
        if (_win is { IsVisible: true }) _win.Hide();
    }

    private void StartHideTimer(int ms)
    {
        CancelHideTimer();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _hideTimer.Tick += (_, _) => Hide();
        _hideTimer.Start();
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
