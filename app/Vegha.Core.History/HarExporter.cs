using System.Text.Json;

namespace Vegha.Core.History;

/// <summary>
/// Emits a minimal HAR 1.2 archive from a list of <see cref="HistoryEntry"/>. We don't
/// have request headers / bodies stored (those live in the request_blob, which is
/// per-tab and not part of the history surface), so the exported entries describe the
/// outgoing call shape (method/URL) and the response (status/body if available).
/// Compatible with Chrome / Firefox / Charles devtools importers.
/// </summary>
public static class HarExporter
{
    public const string HarVersion = "1.2";

    public static string Export(IEnumerable<HistoryEntry> entries, string creatorName = "Vegha", string? creatorVersion = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WritePropertyName("log");
            w.WriteStartObject();
            w.WriteString("version", HarVersion);

            w.WritePropertyName("creator");
            w.WriteStartObject();
            w.WriteString("name", creatorName);
            w.WriteString("version", creatorVersion ?? "0.0");
            w.WriteEndObject();

            w.WritePropertyName("entries");
            w.WriteStartArray();
            foreach (var e in entries) WriteEntry(w, e);
            w.WriteEndArray();

            w.WriteEndObject();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteEntry(Utf8JsonWriter w, HistoryEntry e)
    {
        w.WriteStartObject();
        w.WriteString("startedDateTime", e.TimestampUtc.ToString("O"));
        w.WriteNumber("time", e.DurationMs);

        // Request
        w.WritePropertyName("request");
        w.WriteStartObject();
        w.WriteString("method", e.Method);
        w.WriteString("url", e.Url);
        w.WriteString("httpVersion", "HTTP/1.1");
        w.WritePropertyName("cookies");
        w.WriteStartArray(); w.WriteEndArray();
        w.WritePropertyName("headers");
        w.WriteStartArray(); w.WriteEndArray();
        w.WritePropertyName("queryString");
        w.WriteStartArray(); w.WriteEndArray();
        w.WriteNumber("headersSize", -1);
        w.WriteNumber("bodySize", -1);
        w.WriteEndObject();

        // Response
        w.WritePropertyName("response");
        w.WriteStartObject();
        w.WriteNumber("status", e.StatusCode);
        w.WriteString("statusText", e.ErrorMessage is null ? string.Empty : e.ErrorMessage);
        w.WriteString("httpVersion", "HTTP/1.1");
        w.WritePropertyName("cookies");
        w.WriteStartArray(); w.WriteEndArray();
        w.WritePropertyName("headers");
        w.WriteStartArray(); w.WriteEndArray();
        w.WritePropertyName("content");
        w.WriteStartObject();
        var body = e.ResponseBodyPreview ?? string.Empty;
        w.WriteNumber("size", System.Text.Encoding.UTF8.GetByteCount(body));
        w.WriteString("mimeType", "application/octet-stream");
        if (!string.IsNullOrEmpty(body)) w.WriteString("text", body);
        w.WriteEndObject();
        w.WriteString("redirectURL", string.Empty);
        w.WriteNumber("headersSize", -1);
        w.WriteNumber("bodySize", -1);
        w.WriteEndObject();

        // Cache + timings (placeholders to satisfy strict importers)
        w.WritePropertyName("cache");
        w.WriteStartObject(); w.WriteEndObject();
        w.WritePropertyName("timings");
        w.WriteStartObject();
        w.WriteNumber("send", 0);
        w.WriteNumber("wait", e.DurationMs);
        w.WriteNumber("receive", 0);
        w.WriteEndObject();

        w.WriteEndObject();
    }
}
