using System.Diagnostics;
using System.Drawing;
using System.Windows;
using Swoosh.Updates;
using Forms = System.Windows.Forms;

namespace Swoosh;

public partial class App : System.Windows.Application
{
    private SwooshController? _controller;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _gesturesItem;
    private readonly UpdateChecker _updates = new();
    private string? _updateUrl;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _controller = new SwooshController();
        BuildTray();
        _ = CheckForUpdatesAsync(manual: false);
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

        var updateItem = new Forms.ToolStripMenuItem("Check for updates...");
        updateItem.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);

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
        menu.Items.Add(updateItem);
        menu.Items.Add(aboutItem);
        menu.Items.Add(quitItem);

        _tray = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Swoosh",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.BalloonTipClicked += (_, _) => OpenUpdateUrl();
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        var info = await _updates.CheckAsync();
        if (info != null)
        {
            _updateUrl = info.HtmlUrl;
            _tray?.ShowBalloonTip(
                10000,
                "Swoosh update available",
                $"Version {info.Latest} is available (you have {_updates.CurrentVersion}). " +
                "Click here to download.",
                Forms.ToolTipIcon.Info);
        }
        else if (manual)
        {
            // Only speak up on the "no update" / "couldn't check" paths when the
            // user explicitly asked, so the automatic startup check stays silent.
            _tray?.ShowBalloonTip(
                4000,
                "Swoosh",
                $"You're on the latest version ({_updates.CurrentVersion}).",
                Forms.ToolTipIcon.Info);
        }
    }

    private void OpenUpdateUrl()
    {
        if (string.IsNullOrEmpty(_updateUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch { /* nothing actionable if the shell can't open a browser */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _controller?.Dispose();
        base.OnExit(e);
    }
}
