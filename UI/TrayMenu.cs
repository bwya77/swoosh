using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Swoosh.UI;

// A dark, Todoist-style themed system-tray context menu. It is a real WinForms
// ContextMenuStrip shown through NotifyIcon, so Windows owns the modal menu loop -
// that is what makes it immune to the phantom-touchpad clicks that dismissed the
// custom WPF popup. We only restyle it: dark surface, light text, subtle hover,
// themed separators/checkmark, plus DWM rounded corners and dark mode on the popup.
internal static class TrayMenu
{
    // Active palette (swapped to match the OS light/dark setting each time the menu opens).
    // Defaults to the dark set; ApplyPalette overwrites these before the menu paints.
    private static Color Surface = Color.FromArgb(0x2B, 0x2B, 0x2B);
    private static Color Text = Color.FromArgb(0xE8, 0xE8, 0xE8);
    private static Color TextDim = Color.FromArgb(0x9A, 0x9A, 0x9A);
    private static Color Hover = Color.FromArgb(0x3A, 0x3A, 0x3A);
    private static Color SeparatorClr = Color.FromArgb(0x41, 0x41, 0x41);

    // Dark palette.
    private static readonly Color DarkSurface = Color.FromArgb(0x2B, 0x2B, 0x2B);
    private static readonly Color DarkText = Color.FromArgb(0xE8, 0xE8, 0xE8);
    private static readonly Color DarkTextDim = Color.FromArgb(0x9A, 0x9A, 0x9A);
    private static readonly Color DarkHover = Color.FromArgb(0x3A, 0x3A, 0x3A);
    private static readonly Color DarkSeparator = Color.FromArgb(0x41, 0x41, 0x41);

    // Light palette.
    private static readonly Color LightSurface = Color.FromArgb(0xF9, 0xF9, 0xF9);
    private static readonly Color LightText = Color.FromArgb(0x1A, 0x1A, 0x1A);
    private static readonly Color LightTextDim = Color.FromArgb(0x6A, 0x6A, 0x6A);
    private static readonly Color LightHover = Color.FromArgb(0xE6, 0xE6, 0xE6);
    private static readonly Color LightSeparator = Color.FromArgb(0xD9, 0xD9, 0xD9);

    /// <summary>Point the active palette at the light or dark set.</summary>
    private static void ApplyPalette(bool light)
    {
        Surface = light ? LightSurface : DarkSurface;
        Text = light ? LightText : DarkText;
        TextDim = light ? LightTextDim : DarkTextDim;
        Hover = light ? LightHover : DarkHover;
        SeparatorClr = light ? LightSeparator : DarkSeparator;
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { /* default to dark on any failure */ }
        return false;
    }

    public static ContextMenuStrip Create(
        Func<bool> getGestures,
        Action onSettings,
        Action onToggleGestures,
        Action onTutorial,
        Action onQuit)
    {
        // Match the OS light/dark setting from the start so the first open is themed.
        ApplyPalette(SystemUsesLightTheme());

        var menu = new ContextMenuStrip
        {
            RenderMode = ToolStripRenderMode.Professional,
            ShowImageMargin = true,
            ShowCheckMargin = false,
            DropShadowEnabled = true,
            BackColor = Surface,
            ForeColor = Text,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            Padding = new Padding(0, 7, 0, 7),
            Renderer = new DarkRenderer(),
        };

        var settings = NewItem("Settings", onSettings);

        var gestures = NewItem("Gestures enabled", onToggleGestures);
        gestures.CheckOnClick = false;

        var tutorial = NewItem("Show tutorial", onTutorial);

        var quit = NewItem("Quit Swoosh", onQuit);

        menu.Items.Add(settings);
        menu.Items.Add(gestures);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(tutorial);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);

        // Each time the menu opens: re-read the OS theme so it tracks light/dark live,
        // re-point the menu's own colors, and refresh the toggle state.
        menu.Opening += (_, _) =>
        {
            bool light = SystemUsesLightTheme();
            ApplyPalette(light);
            menu.BackColor = Surface;
            menu.ForeColor = Text;
            gestures.Checked = getGestures();
        };

        menu.HandleCreated += (_, _) => ApplyWindowTheme(menu.Handle);
        menu.Opened += (_, _) => ApplyWindowTheme(menu.Handle);

        return menu;
    }

    private static ToolStripMenuItem NewItem(string text, Action onClick)
    {
        var item = new TallItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    // ContextMenuStrip auto-measures item height from the font/image and largely ignores
    // Padding, so we add vertical breathing room by overriding the preferred size directly.
    private sealed class TallItem : ToolStripMenuItem
    {
        public TallItem(string text) : base(text) { }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            var size = base.GetPreferredSize(constrainingSize);
            size.Height += 14;
            return size;
        }
    }

    private static void ApplyWindowTheme(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int dark = SystemUsesLightTheme() ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private sealed class DarkColors : ProfessionalColorTable
    {
        public DarkColors() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => SeparatorClr;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => SeparatorClr;
        public override Color SeparatorLight => SeparatorClr;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
        public override Color ImageMarginRevealedGradientBegin => Surface;
        public override Color ImageMarginRevealedGradientMiddle => Surface;
        public override Color ImageMarginRevealedGradientEnd => Surface;
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : TextDim;
            // ToolStrip lays text at the top of the (top-aligned) content area; force it to
            // span the full row height so VerticalCenter aligns it with the centered hover pill.
            var tr = e.TextRectangle;
            e.TextRectangle = new Rectangle(tr.X, 0, tr.Width, e.Item.Height);
            e.TextFormat |= TextFormatFlags.VerticalCenter;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            if (e.Item.Selected && e.Item.Enabled)
            {
                var r = new Rectangle(5, 3, e.Item.Width - 10, e.Item.Height - 6);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(r, 5);
                using var b = new SolidBrush(Hover);
                g.FillPath(b, path);
            }
            else
            {
                using var b = new SolidBrush(Surface);
                g.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Surface);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Surface);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var r = e.Item.ContentRectangle;
            int y = r.Top + r.Height / 2;
            using var pen = new Pen(SeparatorClr);
            e.Graphics.DrawLine(pen, r.Left + 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            // Center a fixed-size checkmark box in the check column, vertically centered on
            // the full row height so it lines up with the centered text and hover pill.
            const int box = 16;
            int cx = e.ImageRectangle.Left + e.ImageRectangle.Width / 2;
            int cy = e.Item.Height / 2;
            var r = new Rectangle(cx - box / 2, cy - box / 2, box, box);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Text, 1.7f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            float x = r.Left + r.Width * 0.20f;
            float y = r.Top + r.Height * 0.54f;
            g.DrawLines(pen, new[]
            {
                new PointF(x, y),
                new PointF(x + r.Width * 0.18f, y + r.Height * 0.20f),
                new PointF(x + r.Width * 0.54f, y - r.Height * 0.30f),
            });
        }
    }
}
