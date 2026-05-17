using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vegha.Core.Persistence;

/// <summary>
/// JSON-backed store for <see cref="LayoutSettings"/> at
/// <c>%LocalAppData%/Vegha/layout.json</c> (Win) /
/// <c>~/Library/Application Support/Vegha/layout.json</c> (Mac) /
/// <c>~/.local/share/Vegha/layout.json</c> (Linux).
/// Stand-in until <c>Vegha.Core.Persistence</c> lands.
/// </summary>
public sealed class LayoutSettingsStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly object _writeLock = new();

    public LayoutSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vegha");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "layout.json");
    }

    public LayoutSettings Load()
    {
        if (!File.Exists(_filePath)) return LayoutSettings.Default;
        try
        {
            var json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<LayoutSettings>(json, s_jsonOptions);
            return parsed is null ? LayoutSettings.Default : Clamp(parsed);
        }
        catch
        {
            return LayoutSettings.Default;
        }
    }

    public void Save(LayoutSettings settings)
    {
        lock (_writeLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(Clamp(settings), s_jsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // best-effort; layout is non-critical
            }
        }
    }

    private static LayoutSettings Clamp(LayoutSettings s) => new(
        SidebarWidth: Math.Clamp(s.SidebarWidth, 200, 480),
        RightPanelWidth: Math.Clamp(s.RightPanelWidth, 220, 520),
        ResponsePaneHeight: Math.Clamp(s.ResponsePaneHeight, 160, 640));
}
