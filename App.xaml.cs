using System.Diagnostics;
using System.Drawing;
using System.Windows;
using Swoosh.Settings;
using Swoosh.UI;
using Swoosh.Updates;
using Forms = System.Windows.Forms;

namespace Swoosh;

public partial class App : System.Windows.Application
{
    private SwooshController? _controller;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _gesturesItem;
    private readonly SettingsStore _settings = new();
    private readonly UpdateChecker _updates = new();
    private SettingsWindow? _settingsWindow;
    private string? _updateUrl;
    private bool _syncingTray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _controller = new SwooshController();
        _controller.ApplySettings(_settings.Current);
        _settings.Changed += OnSettingsChanged;

        BuildTray();
        _ = CheckForUpdatesAsync(manual: false);
    }

    private void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();

        var settingsItem = new Forms.ToolStripMenuItem("Settings...");
        settingsItem.Font = new Font(settingsItem.Font, System.Drawing.FontStyle.Bold);
        settingsItem.Click += (_, _) => OpenSettings();

        _gesturesItem = new Forms.ToolStripMenuItem("Gestures enabled")
        {
            Checked = _settings.Current.GesturesEnabled,
            CheckOnClick = true,
        };
        _gesturesItem.CheckedChanged += (_, _) =>
        {
            if (_syncingTray) return;
            var s = _settings.Current.Clone();
            s.GesturesEnabled = _gesturesItem.Checked;
            _settings.Save(s);
        };

        var quitItem = new Forms.ToolStripMenuItem("Quit Swoosh");
        quitItem.Click += (_, _) => Shutdown();

        menu.Items.Add(settingsItem);
        menu.Items.Add(_gesturesItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quitItem);

        _tray = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Swoosh",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) => OpenUpdateUrl();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _updates);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsChanged(AppSettings s)
    {
        _controller?.ApplySettings(s);
        if (_gesturesItem != null && _gesturesItem.Checked != s.GesturesEnabled)
        {
            _syncingTray = true;
            _gesturesItem.Checked = s.GesturesEnabled;
            _syncingTray = false;
        }
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
