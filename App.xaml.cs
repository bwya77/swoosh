using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Swoosh;

public partial class App : System.Windows.Application
{
    private SwooshController? _controller;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _gesturesItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _controller = new SwooshController();
        BuildTray();
    }

    private void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();

        _gesturesItem = new Forms.ToolStripMenuItem("Gestures enabled")
        {
            Checked = true,
            CheckOnClick = true,
        };
        _gesturesItem.CheckedChanged += (_, _) =>
        {
            if (_controller != null) _controller.GesturesEnabled = _gesturesItem.Checked;
        };

        var debugItem = new Forms.ToolStripMenuItem("Touchpad debug overlay");
        debugItem.Click += (_, _) => _controller?.ToggleDebugOverlay();

        var aboutItem = new Forms.ToolStripMenuItem("About Swoosh");
        aboutItem.Click += (_, _) => Forms.MessageBox.Show(
            "Swoosh — Swish-style window gestures for Windows.\n\n" +
            "• Hover a window's titlebar, then two-finger swipe on the touchpad:\n" +
            "   ← left half   → right half   ↑ maximize   ↓ minimize\n" +
            "   diagonals → quarters\n\n" +
            "• Keyboard fallback: Win+Alt+Arrows (halves/max/min),\n" +
            "   Win+Alt+U/I/J/K (quarters).",
            "Swoosh", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);

        var quitItem = new Forms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => Shutdown();

        menu.Items.Add(_gesturesItem);
        menu.Items.Add(debugItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(aboutItem);
        menu.Items.Add(quitItem);

        _tray = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Swoosh",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _controller?.Dispose();
        base.OnExit(e);
    }
}
