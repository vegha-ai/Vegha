using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vegha.Core.Persistence;

/// <summary>JSON store for <see cref="AppSettings"/>. Raises <see cref="Changed"/>
/// on every successful save so consumers (HttpExecutor, HistoryStore, ThemeService)
/// can apply live updates without an app restart.</summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly object _writeLock = new();
    private AppSettings? _cached;

    /// <summary>Raised after a successful <see cref="Save"/>. The handler runs synchronously
    /// on the caller thread (typically the UI thread when invoked from the Settings dialog).</summary>
    public event Action<AppSettings>? Changed;

    public AppSettingsStore()
    {
        // Tests and CI set VEGHA_SETTINGS_DIR to a temp path so runs don't stomp the
        // real user file. Production leaves it unset and falls back to the per-user
        // LocalApplicationData/Vegha directory.
        var dir = Environment.GetEnvironmentVariable("VEGHA_SETTINGS_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vegha");
        }
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
    }

    /// <summary>Returns the current snapshot. Reads from disk on first call, then keeps an
    /// in-memory cache that <see cref="Save"/> refreshes. Callers that want a guaranteed
    /// fresh read should call <see cref="Reload"/>.</summary>
    public AppSettings Load()
    {
        if (_cached is not null) return _cached;
        return _cached = ReadFromDisk();
    }

    public AppSettings Reload() => _cached = ReadFromDisk();

    private AppSettings ReadFromDisk()
    {
        if (!File.Exists(_filePath)) return AppSettings.Default;
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions) ?? AppSettings.Default;
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_writeLock)
        {
            try
            {
                File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, s_jsonOptions));
                _cached = settings;
            }
            catch { /* best-effort */ }
        }
        try { Changed?.Invoke(settings); }
        catch { /* one bad subscriber shouldn't break the save */ }
    }
}
