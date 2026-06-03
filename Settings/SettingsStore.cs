using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Swoosh.Settings;

/// <summary>
/// Loads and persists <see cref="AppSettings"/> as JSON under
/// %APPDATA%\Swoosh\settings.json, and raises <see cref="Changed"/> whenever the
/// settings are saved so the running app can react live (no restart needed).
/// All disk failures are swallowed so a read-only profile never crashes the app.
/// </summary>
public sealed class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Swoosh");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The current, live settings instance.</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>Fired after settings are saved (carries the new snapshot).</summary>
    public event Action<AppSettings>? Changed;

    public SettingsStore() => Load();

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded != null) Current = loaded;
            }
        }
        catch
        {
            Current = new AppSettings(); // corrupt/unreadable: fall back to defaults
        }
    }

    /// <summary>Persist the given settings and notify listeners.</summary>
    public void Save(AppSettings settings)
    {
        Current = settings;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch
        {
            // Non-fatal: settings just won't persist across restarts this time.
        }
        Changed?.Invoke(settings);
    }
}
