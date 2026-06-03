using Microsoft.UI.Xaml;

namespace Swoosh.SettingsApp;

/// <summary>
/// Entry point for the standalone WinUI 3 settings app. The main Swoosh tray app
/// launches this as a separate process (Swoosh.Settings.exe) and the two stay in
/// sync through the shared settings.json file (see SettingsStore's file watcher).
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
