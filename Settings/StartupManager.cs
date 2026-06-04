using System.IO;
using Microsoft.Win32;

namespace Swoosh.Settings;

/// <summary>
/// Reconciles the per-user "run at sign-in" registration with the user's setting. The
/// tray app owns this because it knows its own executable path; the settings app merely
/// flips <see cref="AppSettings.LaunchAtLogin"/> and the running tray app applies it.
/// Uses the standard HKCU Run key so it needs no elevation and is easy for the user to
/// inspect or remove. All failures are swallowed so a locked-down profile can't crash us.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Swoosh";

    /// <summary>Add or remove the HKCU Run entry to match <paramref name="enabled"/>.</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;

            if (enabled)
            {
                string target = ResolveLaunchTarget();
                if (!string.IsNullOrEmpty(target))
                    key.SetValue(ValueName, $"\"{target}\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Best-effort: failing to (de)register startup is non-fatal.
        }
    }

    /// <summary>The Swoosh tray executable to launch at login: prefer the real Swoosh.exe
    /// next to this assembly; fall back to the current process image.</summary>
    private static string ResolveLaunchTarget()
    {
        try
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, "Swoosh.exe");
            if (File.Exists(candidate)) return candidate;

            string? proc = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(proc)) return proc;
        }
        catch
        {
            // fall through to empty
        }
        return string.Empty;
    }
}
