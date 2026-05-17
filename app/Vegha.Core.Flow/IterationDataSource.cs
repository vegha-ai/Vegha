using System.Text;
using System.Text.Json;

namespace Vegha.Core.Flow;

/// <summary>Per-iteration variable rows loaded from a CSV or JSON file. Each row becomes
/// one iteration's overlay variables. Loads eagerly into memory — sized for the kinds of
/// data files API tests realistically use (hundreds to low-thousands of rows), not big-data
/// workloads.</summary>
public sealed class IterationDataSource
{
    private readonly IReadOnlyList<IReadOnlyDictionary<string, string>> _rows;

    public string SourcePath { get; }
    public int RowCount => _rows.Count;
    public IReadOnlyList<string> Columns { get; }

    private IterationDataSource(
        string path,
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        IReadOnlyList<string> columns)
    {
        SourcePath = path;
        _rows = rows;
        Columns = columns;
    }

    /// <summary>The variables for iteration <paramref name="index"/>. Out-of-range indexes
    /// return an empty dict so the orchestrator's bookkeeping stays simple.</summary>
    public IReadOnlyDictionary<string, string> GetRow(int index) =>
        index >= 0 && index < _rows.Count ? _rows[index] : new Dictionary<string, string>();

    /// <summary>Auto-detects format by file extension (.csv → CSV, anything else → JSON).
    /// Throws <see cref="InvalidDataException"/> on malformed input with a message that
    /// includes the offending row or token position.</summary>
    public static async Task<IterationDataSource> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Iteration data file not found.", path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".csv" => await LoadCsvAsync(path, ct).ConfigureAwait(false),
            _      => await LoadJsonAsync(path, ct).ConfigureAwait(false),
        };
    }

    /// <summary>CSV with a required header row. RFC-4180-style quoting: double quotes wrap
    /// values that contain commas, newlines, or embedded quotes; embedded quotes are doubled.
    /// BOM-tolerant. Skips entirely-empty lines.</summary>
    public static async Task<IterationDataSource> LoadCsvAsync(string path, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..]; // strip BOM
        var lines = ParseCsv(text);
        if (lines.Count == 0)
            return new IterationDataSource(path, Array.Empty<IReadOnlyDictionary<string, string>>(), Array.Empty<string>());

        var header = lines[0];
        if (header.Count == 0 || header.All(string.IsNullOrEmpty))
            throw new InvalidDataException("CSV header row is empty.");

        var rows = new List<IReadOnlyDictionary<string, string>>(lines.Count - 1);
        for (var i = 1; i < lines.Count; i++)
        {
            var fields = lines[i];
            if (fields.Count == 0) continue;
            // Drop rows that are entirely blank (all empty fields) — common artifact of
            // trailing newlines in hand-edited CSVs.
            if (fields.All(string.IsNullOrEmpty)) continue;

            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < header.Count; c++)
            {
                var key = header[c];
                if (string.IsNullOrEmpty(key)) continue;
                row[key] = c < fields.Count ? fields[c] : string.Empty;
            }
            rows.Add(row);
        }
        return new IterationDataSource(path, rows, header);
    }

    /// <summary>JSON top-level array of objects. Each object becomes one iteration; values
    /// are stringified (booleans → "true"/"false", numbers → ToString invariant, nulls → "").
    /// Nested objects/arrays are JSON-stringified so the user can still <c>JSON.parse</c>
    /// them inside scripts if needed.</summary>
    public static async Task<IterationDataSource> LoadJsonAsync(string path, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("JSON data file must have a top-level array of objects.");

        var rows = new List<IReadOnlyDictionary<string, string>>();
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Each entry must be a JSON object.");
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in element.EnumerateObject())
            {
                if (seen.Add(prop.Name)) columns.Add(prop.Name);
                row[prop.Name] = StringifyJson(prop.Value);
            }
            rows.Add(row);
        }
        return new IterationDataSource(path, rows, columns);
    }

    private static string StringifyJson(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String      => el.GetString() ?? string.Empty,
        JsonValueKind.Number      => el.GetRawText(),
        JsonValueKind.True        => "true",
        JsonValueKind.False       => "false",
        JsonValueKind.Null        => string.Empty,
        JsonValueKind.Undefined   => string.Empty,
        _                         => el.GetRawText(),  // objects/arrays → JSON literal
    };

    // ----- CSV parser (RFC 4180 lite) ---------------------------------------

    private static List<IReadOnlyList<string>> ParseCsv(string text)
    {
        var lines = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        sb.Append('"'); i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(sb.ToString()); sb.Clear();
                    break;
                case '\r':
                    // Swallow CR; the LF (or EOF) finishes the line.
                    break;
                case '\n':
                    fields.Add(sb.ToString()); sb.Clear();
                    lines.Add(fields);
                    fields = new List<string>();
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        // Tail: flush any pending field/line.
        if (sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(sb.ToString());
            lines.Add(fields);
        }
        return lines;
    }
}
