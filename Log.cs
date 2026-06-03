using System.IO;

namespace Swoosh;

/// <summary>Minimal file logger for diagnosing input/snap behavior.</summary>
public static class Log
{
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swoosh.log");
    private static readonly object Gate = new();

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
