using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Swoosh.Settings;
using Swoosh.Updates;
using Forms = System.Windows.Forms;

namespace Swoosh;

public partial class App : System.Windows.Application
{
    private SwooshController? _controller;
    private Forms.NotifyIcon? _tray;
    private readonly SettingsStore _settings = new();
    private readonly UpdateChecker _updates = new();
    private string? _updateUrl;

    // Per-user single-instance guard. Held for the lifetime of the process so a
    // second launch (e.g. login Run key firing while the app is already up, or a
    // double click) detects us and exits instead of stacking a second tray icon.
    private static Mutex? _instanceMutex;
    private const string InstanceMutexName = "Local\\Swoosh.SingleInstance";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Bail out quietly if another Swoosh is already running for this user.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isNew);
        if (!isNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _controller = new SwooshController();
        _controller.ApplySettings(_settings.Current);
        StartupManager.Apply(_settings.Current.LaunchAtLogin);
        _settings.Changed += OnSettingsChanged;

        BuildTray();
        _ = CheckForUpdatesAsync(manual: false);
    }

    private void BuildTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Swoosh",
            Visible = true,
        };
        // OS-owned dark-themed context menu (immune to the phantom-click dismissal that
        // killed the custom WPF popup - Windows runs the menu's own modal loop).
        _tray.ContextMenuStrip = UI.TrayMenu.Create(
            getGestures: () => _settings.Current.GesturesEnabled,
            onSettings: OpenSettings,
            onToggleGestures: () =>
            {
                var s = _settings.Current.Clone();
                s.GesturesEnabled = !s.GesturesEnabled;
                _settings.Save(s);
            },
            onQuit: () => Shutdown());
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) => OpenUpdateUrl();
    }

    private void OpenSettings()
    {
        try
        {
            // If the settings app is already open, just bring it to the foreground.
            var existing = Process.GetProcessesByName("Swoosh.Settings");
            foreach (var p in existing)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
            }
            if (existing.Length > 0) return; // starting up or has no window yet: don't spawn a second one

            var exe = ResolveSettingsExe();
            if (exe == null)
            {
                _tray?.ShowBalloonTip(4000, "Swoosh",
                    "Settings app (Swoosh.Settings.exe) was not found.", Forms.ToolTipIcon.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
        }
        catch { /* nothing actionable if the settings app can't launch */ }
    }

    /// <summary>
    /// Find Swoosh.Settings.exe: next to the main exe (release layout, optionally in a
    /// "Settings" subfolder), or in the WinUI project's bin folder for local dev runs.
    /// </summary>
    private static string? ResolveSettingsExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "Settings", "Swoosh.Settings.exe"),
            Path.Combine(baseDir, "Swoosh.Settings.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // Dev fallback: walk up to the repo root (has Swoosh.sln), take the newest build.
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Swoosh.sln")))
            dir = dir.Parent;
        if (dir != null)
        {
            var projBin = Path.Combine(dir.FullName, "Swoosh.Settings", "bin");
            if (Directory.Exists(projBin))
            {
                return Directory.GetFiles(projBin, "Swoosh.Settings.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }
        return null;
    }

    private void OnSettingsChanged(AppSettings s)
    {
        // External edits from the settings app arrive on the watcher's background
        // thread; marshal to the UI thread before touching the controller.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSettingsChanged(s));
            return;
        }
        _controller?.ApplySettings(s);
        StartupManager.Apply(s.LaunchAtLogin);
        // The tray flyout is rebuilt from current settings each time it opens, so
        // there is no persistent menu item to keep in sync here.
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        // Local dev builds carry the 0.1.0 sentinel from the csproj (CI stamps the
        // real 0.1.<run> at publish time), so they would always trail the latest
        // release and nag on every launch. Skip the SILENT startup check for them;
        // the manual "Check for updates..." menu item still runs normally.
        if (!manual && IsDevBuild)
            return;

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

    /// <summary>
    /// True for an un-stamped local build: either compiled in Debug, or carrying the
    /// 0.1.0 version sentinel that the release workflow overrides at publish time.
    /// </summary>
    private bool IsDevBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return _updates.CurrentVersion <= new Version(0, 1, 0);
#endif
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _controller?.Dispose();
        _settings.Dispose();
        if (_instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* not owned */ }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
        base.OnExit(e);
    }
}
