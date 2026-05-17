using System.Text.Json;
using Vegha.Core.History;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.History;

public class HarExporterTests
{
    private static HistoryEntry MakeEntry(string method = "GET", string url = "https://api.test/users",
        int status = 200, long durationMs = 137, string? body = "ok") =>
        new(Id: 1,
            TimestampUtc: DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
            Method: method,
            Url: url,
            StatusCode: status,
            DurationMs: durationMs,
            ResponseBodyPreview: body,
            ErrorMessage: null);

    [Fact]
    public void Export_ProducesValidJson_WithLogVersion12()
    {
        var har = HarExporter.Export(new[] { MakeEntry() });
        using var doc = JsonDocument.Parse(har);
        doc.RootElement.GetProperty("log").GetProperty("version").GetString().Should().Be("1.2");
    }

    [Fact]
    public void Export_PerEntry_HasRequestAndResponseBlocks()
    {
        var har = HarExporter.Export(new[] { MakeEntry(method: "POST", url: "https://api.test/x", status: 201) });
        using var doc = JsonDocument.Parse(har);
        var entry = doc.RootElement.GetProperty("log").GetProperty("entries")[0];
        entry.GetProperty("request").GetProperty("method").GetString().Should().Be("POST");
        entry.GetProperty("request").GetProperty("url").GetString().Should().Be("https://api.test/x");
        entry.GetProperty("response").GetProperty("status").GetInt32().Should().Be(201);
    }

    [Fact]
    public void Export_ResponseContent_HasBody_WhenPreviewPresent()
    {
        var har = HarExporter.Export(new[] { MakeEntry(body: "{\"ok\":true}") });
        using var doc = JsonDocument.Parse(har);
        var content = doc.RootElement.GetProperty("log").GetProperty("entries")[0]
            .GetProperty("response").GetProperty("content");
        content.GetProperty("text").GetString().Should().Be("{\"ok\":true}");
        content.GetProperty("size").GetInt32().Should().Be(11);
    }

    [Fact]
    public void Export_Timings_PutWaitOnDuration()
    {
        var har = HarExporter.Export(new[] { MakeEntry(durationMs: 250) });
        using var doc = JsonDocument.Parse(har);
        var timings = doc.RootElement.GetProperty("log").GetProperty("entries")[0]
            .GetProperty("timings");
        timings.GetProperty("wait").GetInt64().Should().Be(250);
    }

    [Fact]
    public void Export_Empty_StillProducesValidLogShape()
    {
        var har = HarExporter.Export(Array.Empty<HistoryEntry>());
        using var doc = JsonDocument.Parse(har);
        doc.RootElement.GetProperty("log").GetProperty("entries").GetArrayLength().Should().Be(0);
    }
}
