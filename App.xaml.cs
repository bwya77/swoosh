using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    private System.Drawing.Icon? _trayIcon;
    private readonly SettingsStore _settings = new();
    private readonly UpdateChecker _updates = new();
    private string? _updateUrl;
    private string? _installerUrl;
    private string? _latestVersion;

    // Per-user single-instance guard. Held for the lifetime of the process so a
    // second launch (e.g. login Run key firing while the app is already up, or a
    // double click) detects us and exits instead of stacking a second tray icon.
    private static Mutex? _instanceMutex;
    private const string InstanceMutexName = "Local\\Swoosh.SingleInstance";

    // Cross-process "show the tutorial" signal. The Settings app (a separate process) sets this
    // named event when the user clicks Replay tutorial; this app listens and shows onboarding.
    private const string TutorialSignalName = @"Local\Swoosh_Show_Tutorial_v1";
    private EventWaitHandle? _tutorialSignal;
    private Thread? _tutorialThread;
    private volatile bool _shuttingDown;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hidden developer path: render the tutorial demo animations to PNG frame sequences for
        // building the README GIFs, then exit. Does not start the tray.
        int exportIdx = Array.FindIndex(e.Args, a => string.Equals(a, "--export-tutorial", StringComparison.OrdinalIgnoreCase));
        if (exportIdx >= 0)
        {
            string outDir = exportIdx + 1 < e.Args.Length ? e.Args[exportIdx + 1]
                : Path.Combine(Path.GetTempPath(), "swoosh-tutorial-frames");
            RunTutorialExport(outDir);
            return;
        }

        // Bail out quietly if another Swoosh is already running for this user.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isNew);
        if (!isNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        // The installer launches us with --enable-startup when the user ticked
        // "Start Swoosh when I sign in". Persist it as the LaunchAtLogin setting so the
        // app (the single owner of the Run key) registers it on this very launch.
        if (e.Args.Any(a => string.Equals(a, "--enable-startup", StringComparison.OrdinalIgnoreCase))
            && !_settings.Current.LaunchAtLogin)
        {
            var s = _settings.Current.Clone();
            s.LaunchAtLogin = true;
            _settings.Save(s);
        }

        _controller = new SwooshController();
        _controller.ApplySettings(_settings.Current);
        StartupManager.Apply(_settings.Current.LaunchAtLogin);
        _settings.Changed += OnSettingsChanged;

        BuildTray();
        _ = CheckForUpdatesAsync(manual: false);

        // First-run tutorial: show the visual gesture walkthrough once.
        if (!_settings.Current.OnboardingCompleted)
            Dispatcher.BeginInvoke(ShowOnboarding, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        StartTutorialListener();

        // Probe the touchpad and write a diagnostics report for the Settings "Copy
        // diagnostics" button. If no usable Precision Touchpad is found, warn the user
        // (instead of silently doing nothing). Off the UI-critical path.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            Diagnostics.WriteStartupReport();
            if (!Diagnostics.TouchpadDetected)
            {
                Dispatcher.BeginInvoke(() => _tray?.ShowBalloonTip(
                    9000, "Swoosh: no Precision Touchpad found",
                    "Swoosh needs a Windows Precision Touchpad. External mice and older (non-precision) touchpads aren't supported, so gestures won't work on this PC.",
                    Forms.ToolTipIcon.Warning));
            }
        });
    }

    /// <summary>Load the app icon for the tray: prefer the embedded multi-resolution
    /// .ico (Windows picks the right size for the tray), fall back to the icon baked
    /// into the executable, then to the generic system icon.</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("swoosh.ico");
            if (s != null) return new System.Drawing.Icon(s);
        }
        catch { /* fall through */ }

        try
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p))
            {
                var i = System.Drawing.Icon.ExtractAssociatedIcon(p);
                if (i != null) return i;
            }
        }
        catch { /* fall through */ }

        return SystemIcons.Application;
    }

    private void BuildTray()
    {
        _trayIcon = LoadAppIcon();
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Swoosh",
            Visible = true,
        };
        // OS-owned dark-themed context menu (immune to the phantom-click dismissal that
        // killed the custom WPF popup - Windows runs the menu's own modal loop).
        _tray.ContextMenuStrip = UI.TrayMenu.Create(
            getGestures: () => _settings.Current.GesturesEnabled,
            getUpdate: () => (!string.IsNullOrEmpty(_updateUrl), _latestVersion),
            onUpdate: OnUpdateClicked,
            onCheckUpdates: () => _ = CheckForUpdatesAsync(manual: true),
            onSettings: OpenSettings,
            onToggleGestures: () =>
            {
                var s = _settings.Current.Clone();
                s.GesturesEnabled = !s.GesturesEnabled;
                _settings.Save(s);
            },
            onTutorial: ShowOnboarding,
            onQuit: () => Shutdown());
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) => OnUpdateClicked();
    }

    private UI.OnboardingWindow? _onboarding;

    /// <summary>Background listener for the cross-process "show tutorial" event set by the
    /// Settings app, so users can replay the walkthrough from Settings.</summary>
    private void StartTutorialListener()
    {
        try
        {
            _tutorialSignal = new EventWaitHandle(false, EventResetMode.AutoReset, TutorialSignalName);
            _tutorialThread = new Thread(() =>
            {
                while (!_shuttingDown && _tutorialSignal != null)
                {
                    try
                    {
                        if (_tutorialSignal.WaitOne(1000) && !_shuttingDown)
                            Dispatcher.BeginInvoke(ShowOnboarding);
                    }
                    catch { return; }
                }
            })
            { IsBackground = true, Name = "SwooshTutorialSignal" };
            _tutorialThread.Start();
        }
        catch { /* signal unavailable: the tray "Show tutorial" item still works */ }
    }

    /// <summary>Show the first-run gesture tutorial. Reused by the tray "Show tutorial" item.
    /// Marks onboarding complete when finished so it doesn't reappear on next launch.</summary>
    private void ShowOnboarding()
    {
        try
        {
            if (_onboarding != null) { _onboarding.Activate(); return; }

            var accent = UI.AccentColors.Resolve(
                _settings.Current.OverlayUseAccent, _settings.Current.OverlayColor);
            _onboarding = new UI.OnboardingWindow(accent);
            _onboarding.Completed += () =>
            {
                if (!_settings.Current.OnboardingCompleted)
                {
                    var s = _settings.Current.Clone();
                    s.OnboardingCompleted = true;
                    _settings.Save(s);
                }
            };
            _onboarding.Closed += (_, _) => _onboarding = null;
            _onboarding.Show();
            _onboarding.Activate();
        }
        catch (Exception ex) { Log.Write($"Onboarding failed: {ex}"); }
    }

    /// <summary>Render each tutorial demo step's animation to a PNG frame sequence (one folder per
    /// gesture) for assembling README GIFs. Uses RenderTargetBitmap for crisp, deterministic
    /// frames, then exits.</summary>
    private void RunTutorialExport(string outDir)
    {
        var accent = UI.AccentColors.Resolve(true, "#0A84FF");
        var win = new UI.OnboardingWindow(accent)
        {
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
        };
        win.Show();
        win.ForceDarkForExport();

        // Let the visual tree lay out, then render off the dispatcher queue.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { ExportTutorialFrames(win, outDir); }
            catch (Exception ex) { Log.Write($"Tutorial export failed: {ex}"); }
            finally { try { win.Close(); } catch { } Shutdown(); }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void ExportTutorialFrames(UI.OnboardingWindow win, string outDir)
    {
        const int fps = 25;
        const double scale = 2.0; // render at 2x for crisp downscaling
        Directory.CreateDirectory(outDir);

        for (int i = 0; i < win.StepCount; i++)
        {
            if (!win.StepHasDemo(i)) continue;

            var el = win.PrepareExport(i);
            el.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            el.Arrange(new System.Windows.Rect(new System.Windows.Point(0, 0), el.DesiredSize));
            el.UpdateLayout();
            var size = el.RenderSize;
            if (size.Width < 1 || size.Height < 1) continue;

            string key = win.StepKey(i);
            string dir = Path.Combine(outDir, key);
            Directory.CreateDirectory(dir);

            double cycle = win.StepCycleMs(i);
            int frames = Math.Max(1, (int)Math.Round(cycle / 1000.0 * fps));

            for (int f = 0; f < frames; f++)
            {
                double t = cycle * f / frames;
                win.RenderFrameAt(t);
                el.Arrange(new System.Windows.Rect(new System.Windows.Point(0, 0), size));
                el.UpdateLayout();

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(size.Width * scale),
                    (int)Math.Ceiling(size.Height * scale),
                    96 * scale, 96 * scale,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(el);

                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(dir, $"f{f:D3}.png"));
                enc.Save(fs);
            }
            Log.Write($"Exported {frames} frames for '{key}'");
        }
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
    /// Find Swoosh.Settings.exe and return the NEWEST build available: next to the main exe
    /// (release layout, optionally in a "Settings" subfolder) and, for local dev runs, any
    /// build under the WinUI project's bin folder. A Settings.exe co-located with the running
    /// tray app is ALWAYS preferred (installed and published builds ship the two together, so
    /// they share a version); the dev-bin scan is only a fallback for a pure source run where no
    /// co-located Settings exists. This avoids an installed app ever launching a different
    /// version's Settings (e.g. a stale dev build) on a developer's machine.
    /// </summary>
    private static string? ResolveSettingsExe()
    {
        string baseDir = AppContext.BaseDirectory;

        // Release/installed layout: the exe sits next to the main app (optionally in a
        // subfolder). If present, this is the version-matched companion: use it, full stop.
        var nextToExe = new[]
        {
            Path.Combine(baseDir, "Settings", "Swoosh.Settings.exe"),
            Path.Combine(baseDir, "Swoosh.Settings.exe"),
        };
        var coLocated = nextToExe.FirstOrDefault(File.Exists);
        if (coLocated != null) return coLocated;

        // Pure dev run (the tray app's own bin has no co-located Settings): walk up to the repo
        // root (has Swoosh.sln) and pick the newest build under the settings project's bin.
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Swoosh.sln")))
            dir = dir.Parent;
        if (dir != null)
        {
            var projBin = Path.Combine(dir.FullName, "Swoosh.Settings", "bin");
            if (Directory.Exists(projBin))
            {
                try
                {
                    return Directory.GetFiles(projBin, "Swoosh.Settings.exe", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                }
                catch { /* ignore enumeration failures */ }
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
            _installerUrl = info.InstallerUrl;
            _latestVersion = info.Latest?.ToString();
            _tray?.ShowBalloonTip(
                10000,
                "Swoosh update available",
                $"Version {info.Latest} is available (you have {_updates.CurrentVersion}). " +
                "Click here to update.",
                Forms.ToolTipIcon.Info);
        }
        else if (manual)
        {
            // Up to date: clear any stale update state so the tray item hides.
            _updateUrl = null;
            _installerUrl = null;
            _latestVersion = null;
            _tray?.ShowBalloonTip(
                4000,
                "Swoosh",
                $"You're on the latest version ({_updates.CurrentVersion}).",
                Forms.ToolTipIcon.Info);
        }
    }

    /// <summary>Act on the "update available" balloon or tray item. An installed build downloads
    /// and runs the signed installer (which closes and relaunches Swoosh); a portable or dev
    /// build opens the releases page. Logs which path is taken so fallbacks are diagnosable.</summary>
    private async void OnUpdateClicked()
    {
        bool installed = IsInstalled();
        bool haveInstaller = !string.IsNullOrEmpty(_installerUrl);
        Log.Write($"Update clicked: installed={installed} haveInstaller={haveInstaller} url={_installerUrl}");

        if (installed && haveInstaller)
        {
            _tray?.ShowBalloonTip(4000, "Swoosh", "Downloading the update...", Forms.ToolTipIcon.Info);
            if (await TryRunInstallerAsync(_installerUrl!))
            {
                Log.Write("Update: installer launched");
                return;
            }
            // In-place update failed (download error, or the elevation prompt was declined):
            // fall back to the releases page and say so, instead of failing silently.
            _tray?.ShowBalloonTip(5000, "Swoosh",
                "Could not start the in-app update. Opening the download page instead.",
                Forms.ToolTipIcon.Warning);
            Log.Write("Update: installer launch failed, opening releases page");
        }

        OpenUpdateUrl();
    }

    /// <summary>True when running from a Program Files install (vs an extracted portable zip).</summary>
    private static bool IsInstalled()
    {
        try
        {
            string? p = Environment.ProcessPath;
            if (string.IsNullOrEmpty(p)) return false;
            foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(root) && p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* fall through */ }
        return false;
    }

    /// <summary>Download the signed installer to a temp file and launch it silently. The
    /// installer's Restart Manager support closes the running app, updates in place, and
    /// relaunches it, so the update applies with just the one elevation prompt.</summary>
    private static async Task<bool> TryRunInstallerAsync(string url)
    {
        try
        {
            string dest = Path.Combine(Path.GetTempPath(), $"SwooshSetup-{Guid.NewGuid():N}.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var resp = await http.GetAsync(url))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(dest);
                await resp.Content.CopyToAsync(fs);
            }
            // /VERYSILENT runs the Inno Setup installer without its wizard; Restart Manager
            // (CloseApplications/RestartApplications) closes and relaunches Swoosh. The admin
            // manifest still triggers a single UAC prompt via ShellExecute.
            Process.Start(new ProcessStartInfo(dest)
            {
                UseShellExecute = true,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES",
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Installer launch failed: {ex.Message}");
            return false;
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
        _shuttingDown = true;
        try { _tutorialSignal?.Set(); } catch { /* wake the loop so it can exit */ }
        _tutorialThread?.Join(500);
        _tutorialSignal?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _trayIcon?.Dispose();
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
