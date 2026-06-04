using System.IO;
using Microsoft.Win32;

namespace Swoosh.Settings;

/// <summary>
/// Reconciles the per-user "run at sign-in" registration with the user's setting. The
/// tray app owns this because it knows its own executable path; the settings app merely
/// flips <see cref="AppSettings.LaunchAtLogin"/> and the running tray app applies it.
/// Uses the standard HKCU Run key so it needs no elevation and is easy for the user to
/// inspect or remove. All failures are swallowed so a locked-down profile can't crash us.
///
/// <para><b>Self-heal:</b> the tray app calls <see cref="Apply"/> on every launch (and on
/// every settings change). When enabled, that re-points the Run entry at the executable's
/// <em>current</em> location, so if the user moves or updates the app the registration
/// repairs itself the first time the moved copy runs — the only missed launch is the one
/// sign-in between moving and first running from the new path.</para>
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Swoosh";

    /// <summary>Reconcile the HKCU Run entry with <paramref name="enabled"/>, repairing a
    /// drifted path in place. Writes only when something actually needs to change, and logs
    /// each add/repair/remove so the self-heal is observable.</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;

            string? current = key.GetValue(ValueName) as string;

            if (enabled)
            {
                string target = ResolveLaunchTarget();
                if (string.IsNullOrEmpty(target))
                {
                    Log.Write("StartupManager: enabled but could not resolve launch target");
                    return;
                }

                string desired = $"\"{target}\"";
                if (current == null)
                {
                    key.SetValue(ValueName, desired);
                    Log.Write($"StartupManager: registered startup -> {desired}");
                }
                else if (!string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, desired);
                    Log.Write($"StartupManager: repaired startup path {current} -> {desired}");
                }
                // else: already correct, leave the registry untouched.
            }
            else if (current != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Write("StartupManager: unregistered startup");
            }
        }
        catch
        {
            // Best-effort: failing to (de)register startup is non-fatal.
        }
    }

    /// <summary>The Swoosh tray executable to launch at login: prefer the real Swoosh.exe
    /// next to this assembly; fall back to the current process image. For a single-file
    /// publish <see cref="AppContext.BaseDirectory"/> is the executable's own folder, so
    /// this resolves to the app's current location even after the user moves it.</summary>
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
