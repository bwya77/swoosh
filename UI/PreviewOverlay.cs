using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Swoosh.Native;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontWeights = System.Windows.FontWeights;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Swoosh.UI;

/// <summary>
/// Topmost, click-through translucent window that previews the snap target zone.
/// Positioned in physical pixels via SetWindowPos so it is DPI-correct across
/// monitors regardless of WPF's logical coordinate system. The rectangle glides
/// between zones (ease-out cubic) to mirror the window-move animation.
/// </summary>
public sealed class PreviewOverlay
{
    private Window? _win;
    private Border? _border;
    private TextBlock? _label;

    // Appearance, applied live from settings.
    private bool _animate = true;
    private Color _fill = Color.FromRgb(0, 120, 215);
    // Highlight color source, kept so the accent can be re-resolved live (throttled) when the
    // user changes their Windows accent color, without restarting.
    private bool _useAccent = true;
    private string _customHex = "#0A84FF";
    private long _lastAccentCheckMs;

    // Position glide (mirrors WindowSnapper.AnimateTo: ease-out cubic over GlideMs).
    // Driven off CompositionTarget.Rendering so frames are vsync-paced and evenly
    // spaced; timeBeginPeriod(1) keeps the system scheduler fine-grained meanwhile.
    private double _glideMs = 200;
    private readonly Stopwatch _clock = new();
    private bool _gliding;
    private bool _timerRaised;
    private double _sx, _sy, _sw, _sh;   // glide start rect
    private double _tx, _ty, _tw, _th;   // glide target rect
    private double _cx, _cy, _cw, _ch;   // current on-screen rect
    private bool _hasRect;

    /// <summary>Apply live appearance: glide on/off, the highlight color, and the glide
    /// duration in ms (matched to the window-move speed).</summary>
    public void ApplyAppearance(bool animate, bool useAccent, string customHex, double glideMs)
    {
        _animate = animate;
        _useAccent = useAccent;
        _customHex = customHex;
        _fill = AccentColors.Resolve(useAccent, customHex);
        _glideMs = Math.Clamp(glideMs, 50, 500);
    }

    /// <summary>When following the Windows accent, re-read it (throttled) so a changed accent
    /// shows on the next preview without restarting.</summary>
    private void SyncAccentColor()
    {
        if (!_useAccent) return;
        long now = Environment.TickCount64;
        if (now - _lastAccentCheckMs < 750) return;
        _lastAccentCheckMs = now;
        _fill = AccentColors.Resolve(true, _customHex);
    }

    private void EnsureWindow()
    {
        if (_win != null) return;

        _label = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Text = string.Empty,
        };

        _border = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(230, 0, 120, 215)),
            Background = new SolidColorBrush(Color.FromArgb(70, 0, 120, 215)),
            Margin = new Thickness(6),
            Child = _label,
        };

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
            Content = _border,
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

    public void ShowZone(Win32.RECT rect, double progress)
    {
        SyncAccentColor();
        EnsureWindow();
        if (_win == null || _border == null || _label == null) return;

        byte fill = (byte)(40 + 60 * Math.Clamp(progress, 0, 1));
        _border.BorderBrush = new SolidColorBrush(Color.FromArgb(230, _fill.R, _fill.G, _fill.B));
        _border.Background = new SolidColorBrush(Color.FromArgb(fill, _fill.R, _fill.G, _fill.B));
        _label.Text = string.Empty;

        GlideTo(rect);
    }

    /// <summary>Show a centered hold-mode banner (purple) with directional emphasis.</summary>
    public void ShowHint(Win32.RECT area, string text)
    {
        EnsureWindow();
        if (_win == null || _border == null || _label == null) return;

        _border.BorderBrush = new SolidColorBrush(Color.FromArgb(235, 150, 80, 220));
        _border.Background = new SolidColorBrush(Color.FromArgb(180, 90, 40, 150));
        _label.Text = text;

        // The hold banner appears instantly (no glide between unrelated areas).
        StopGlide();
        if (!_win.IsVisible) _win.Show();
        SetPos(area.Left, area.Top, area.Width, area.Height);
        _sx = _tx = _cx; _sy = _ty = _cy; _sw = _tw = _cw; _sh = _th = _ch;
        _hasRect = true;
    }

    private void GlideTo(Win32.RECT rect)
    {
        if (!_win!.IsVisible) _win.Show();

        double tx = rect.Left, ty = rect.Top, tw = rect.Width, th = rect.Height;

        // First placement of this gesture, or glide disabled: jump straight there.
        if (!_animate || !_hasRect)
        {
            StopGlide();
            SetPos(tx, ty, tw, th);
            _sx = _tx = tx; _sy = _ty = ty; _sw = _tw = tw; _sh = _th = th;
            _hasRect = true;
            return;
        }

        // Same target as the current placement: only the fill alpha changed, no move.
        if (Math.Abs(tx - _tx) < 1 && Math.Abs(ty - _ty) < 1 &&
            Math.Abs(tw - _tw) < 1 && Math.Abs(th - _th) < 1)
            return;

        // Glide from where the rectangle currently sits to the new zone.
        _sx = _cx; _sy = _cy; _sw = _cw; _sh = _ch;
        _tx = tx; _ty = ty; _tw = tw; _th = th;

        _clock.Restart();
        if (!_gliding)
        {
            _gliding = true;
            if (!_timerRaised) { Win32.timeBeginPeriod(1); _timerRaised = true; }
            CompositionTarget.Rendering += OnRendering;
        }
        OnRendering(null, EventArgs.Empty); // render the first frame now
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double t = _glideMs <= 0 ? 1.0 : _clock.Elapsed.TotalMilliseconds / _glideMs;
        if (t >= 1.0) t = 1.0;
        double p = 1.0 - Math.Pow(1.0 - t, 3); // ease-out cubic

        SetPos(_sx + (_tx - _sx) * p, _sy + (_ty - _sy) * p,
               _sw + (_tw - _sw) * p, _sh + (_th - _sh) * p);

        if (t >= 1.0) { SetPos(_tx, _ty, _tw, _th); StopGlide(); }
    }

    private void SetPos(double x, double y, double w, double h)
    {
        _cx = x; _cy = y; _cw = w; _ch = h;
        IntPtr hh = new WindowInteropHelper(_win!).Handle;
        Win32.SetWindowPos(hh, Win32.HWND_TOPMOST,
            (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h),
            Win32.SWP_NOACTIVATE);
    }

    private void StopGlide()
    {
        if (_gliding)
        {
            CompositionTarget.Rendering -= OnRendering;
            _gliding = false;
        }
        if (_timerRaised) { Win32.timeEndPeriod(1); _timerRaised = false; }
        _clock.Reset();
    }

    public void Hide()
    {
        StopGlide();
        _hasRect = false;
        if (_win is { IsVisible: true }) _win.Hide();
    }

    public void Close()
    {
        StopGlide();
        _win?.Close();
        _win = null;
    }
}
