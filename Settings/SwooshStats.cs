using System.IO;
using System.Text.Json;

namespace Swoosh.Settings;

/// <summary>
/// Tracks the lifetime count of "swooshes" (committed window gestures) in a tiny JSON
/// file kept separate from settings.json. Keeping it apart means the high-frequency
/// counter writes from the tray app never race the settings app's read-modify-write of
/// the settings file. The tray app is the sole writer (via <see cref="Add"/>); the
/// settings app reads on load and watches for live updates to display the running total.
/// All disk failures are swallowed so a read-only profile never crashes the app.
/// </summary>
public sealed class SwooshStats : IDisposable
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Swoosh");
    private static readonly string FilePath = Path.Combine(Dir, "stats.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;

    /// <summary>The lifetime number of committed swooshes.</summary>
    public long LifetimeSwooshes { get; private set; }

    /// <summary>Fired (on a background thread) when the on-disk count changes, for live
    /// display. Subscribers that touch UI must marshal to their UI thread.</summary>
    public event Action<long>? Changed;

    public SwooshStats() => Load();

    /// <summary>Begin watching stats.json so <see cref="Changed"/> fires when another
    /// process (the tray app) updates the count. Only the reader (settings app) needs this.</summary>
    public void StartWatching()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            _watcher = new FileSystemWatcher(Dir, "stats.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
        catch
        {
            _watcher = null; // live updates are best-effort
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        long prev = LifetimeSwooshes;
        Load();
        if (LifetimeSwooshes != prev) Changed?.Invoke(LifetimeSwooshes);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var data = JsonSerializer.Deserialize<StatsData>(sr.ReadToEnd());
            if (data != null) LifetimeSwooshes = data.LifetimeSwooshes;
        }
        catch
        {
            // Corrupt/unreadable: keep whatever we already had (or zero).
        }
    }

    /// <summary>Add to the lifetime count and persist atomically. The tray app is the
    /// sole writer, so there is no cross-process write contention.</summary>
    public void Add(long n = 1)
    {
        if (n <= 0) return;
        lock (_gate)
        {
            LifetimeSwooshes += n;
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(new StatsData { LifetimeSwooshes = LifetimeSwooshes }, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch
        {
            // Non-fatal: the count just won't persist this time.
        }
    }

    private sealed class StatsData
    {
        public long LifetimeSwooshes { get; set; }
    }

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
