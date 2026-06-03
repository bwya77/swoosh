using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Swoosh.Input;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace Swoosh.UI;

/// <summary>
/// Small always-on-top panel that visualizes raw touchpad contacts, so finger
/// tracking can be validated on real hardware. Shows a live finger-count readout
/// plus each contact's normalized position and id.
/// </summary>
public sealed class DebugOverlay
{
    private Window? _win;
    private Canvas? _canvas;
    private TextBlock? _readout;
    private const double PadW = 320, PadH = 200;

    public bool IsVisible => _win is { IsVisible: true };

    private void Ensure()
    {
        if (_win != null) return;
        _canvas = new Canvas { Width = PadW, Height = PadH, ClipToBounds = true };

        var pad = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 14, 14, 18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 90, 90, 110)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = _canvas,
        };

        _readout = new TextBlock
        {
            Text = "Fingers: 0",
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            Margin = new Thickness(2, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(_readout);
        stack.Children.Add(pad);

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(228, 20, 20, 24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
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
            Title = "Swoosh Touchpad Debug",
        };
        var work = SystemParameters.WorkArea;
        _win.Left = work.Right - PadW - 48;
        _win.Top = work.Bottom - PadH - 80;
    }

    public void Toggle()
    {
        Ensure();
        if (_win!.IsVisible) _win.Hide();
        else _win.Show();
    }

    /// <summary>Force the overlay to a specific visibility (used by settings).</summary>
    public void SetVisible(bool visible)
    {
        if (!visible && _win == null) return; // nothing to hide yet
        Ensure();
        if (visible) _win!.Show();
        else _win!.Hide();
    }

    public void Render(TouchFrame frame)
    {
        if (_win is not { IsVisible: true } || _canvas == null) return;

        int down = frame.DownCount;
        if (_readout != null)
        {
            _readout.Text = $"Fingers: {down}";
            _readout.Foreground = down switch
            {
                2 => new SolidColorBrush(Color.FromRgb(0, 220, 130)),   // gesture-ready
                >= 3 => new SolidColorBrush(Color.FromRgb(255, 170, 60)), // too many
                _ => Brushes.White,
            };
        }

        _canvas.Children.Clear();
        foreach (var c in frame.Contacts)
        {
            if (!c.TipDown) continue;
            double cx = c.X * PadW, cy = c.Y * PadH;

            var dot = new Ellipse
            {
                Width = 28, Height = 28,
                Fill = new SolidColorBrush(Color.FromArgb(235, 0, 200, 120)),
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(dot, cx - 14);
            Canvas.SetTop(dot, cy - 14);
            _canvas.Children.Add(dot);

            var label = new TextBlock
            {
                Text = c.Id.ToString(),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
            };
            Canvas.SetLeft(label, cx - 4);
            Canvas.SetTop(label, cy - 9);
            _canvas.Children.Add(label);
        }
    }

    public void Close() { _win?.Close(); _win = null; }
}
