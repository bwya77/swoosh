using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
/// monitors regardless of WPF's logical coordinate system.
/// </summary>
public sealed class PreviewOverlay
{
    private Window? _win;
    private Border? _border;
    private TextBlock? _label;

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
        EnsureWindow();
        if (_win == null || _border == null || _label == null) return;

        byte fill = (byte)(40 + 60 * Math.Clamp(progress, 0, 1));
        _border.BorderBrush = new SolidColorBrush(Color.FromArgb(230, 0, 120, 215));
        _border.Background = new SolidColorBrush(Color.FromArgb(fill, 0, 120, 215));
        _label.Text = string.Empty;

        Place(rect);
    }

    /// <summary>Show a centered hold-mode banner (purple) with directional emphasis.</summary>
    public void ShowHint(Win32.RECT area, string text)
    {
        EnsureWindow();
        if (_win == null || _border == null || _label == null) return;

        _border.BorderBrush = new SolidColorBrush(Color.FromArgb(235, 150, 80, 220));
        _border.Background = new SolidColorBrush(Color.FromArgb(180, 90, 40, 150));
        _label.Text = text;

        Place(area);
    }

    private void Place(Win32.RECT rect)
    {
        if (!_win!.IsVisible) _win.Show();
        IntPtr h = new WindowInteropHelper(_win).Handle;
        Win32.SetWindowPos(h, Win32.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
            Win32.SWP_NOACTIVATE);
    }

    public void Hide()
    {
        if (_win is { IsVisible: true }) _win.Hide();
    }

    public void Close()
    {
        _win?.Close();
        _win = null;
    }
}
