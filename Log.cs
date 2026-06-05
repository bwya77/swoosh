using System.IO;

namespace Swoosh;

/// <summary>Minimal file logger for diagnosing input/snap behavior.</summary>
public static class Log
{
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swoosh.log");
    private static readonly object Gate = new();

    /// <summary>
    /// When true, high-frequency per-frame input diagnostics are emitted. Off by
    /// default: the touchpad decoder runs on the real-time input thread at up to
    /// ~1 kHz, where formatting diagnostic strings and writing them to disk every
    /// frame would dominate the hot path. Enable by setting the SWOOSH_LOG
    /// environment variable (1, true, yes, or on) before launching Swoosh.
    /// </summary>
    public static readonly bool Verbose = ParseVerbose();

    private static bool ParseVerbose()
    {
        var v = Environment.GetEnvironmentVariable("SWOOSH_LOG")?.Trim();
        if (string.IsNullOrEmpty(v)) return false;
        return v == "1"
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }
}
