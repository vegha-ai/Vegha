using System.Text.Json;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

/// <summary>JSON deserialization sanity checks for <see cref="AppSettings"/>. The record's
/// many new optional parameters mean older settings.json files (only a handful of fields)
/// must still deserialize via System.Text.Json's default-parameter handling — these tests
/// pin that promise so we don't accidentally make a new field required.</summary>
public class AppSettingsBackCompatTests
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Deserialize_OldShape_FillsNewFieldsWithDefaults()
    {
        // settings.json written by the previous version of the app — only the original
        // positional fields are present.
        var oldJson = """
            {
              "Theme": "light",
              "FontFamily": "Cascadia Mono",
              "FontSize": 13,
              "HttpProxy": null,
              "DefaultFollowRedirects": true,
              "DefaultVerifySsl": false,
              "DefaultSendCookies": true,
              "DefaultSaveCookies": true,
              "DefaultEncodeUrl": true,
              "RequestTimeoutSeconds": 45
            }
            """;

        var parsed = JsonSerializer.Deserialize<AppSettings>(oldJson, s_options);

        parsed.Should().NotBeNull();
        parsed!.Theme.Should().Be("light");
        parsed.FontSize.Should().Be(13);
        parsed.RequestTimeoutSeconds.Should().Be(45);

        // The new fields take their defaults from the record's parameter list.
        parsed.ThemeMode.Should().Be("dark");
        parsed.InterfaceZoom.Should().Be(1.0);
        parsed.ProxyMode.Should().Be("off");
        parsed.SaveResponsesToHistory.Should().BeFalse();
        parsed.MaxBodySizeMb.Should().Be(50);
        parsed.HistoryRetentionDays.Should().Be(365);
        parsed.HistoryRetentionMaxEntries.Should().Be(1000);
        parsed.EditorTabSize.Should().Be(2);
        parsed.AutoCheckForUpdates.Should().BeTrue();
        parsed.UpdateChannel.Should().Be("stable");
    }

    [Fact]
    public void Deserialize_NewShape_RoundTrips()
    {
        var settings = AppSettings.Default with
        {
            ThemeMode = "system",
            ThemeVariantDark = "DarkCatppuccin",
            InterfaceZoom = 1.25,
            ProxyMode = "on",
            ProxyProtocol = "https",
            ProxyHost = "proxy.example.com",
            ProxyPort = 8443,
            SaveResponsesToHistory = true,
            MaxBodySizeMb = 100,
        };

        var json = JsonSerializer.Serialize(settings, s_options);
        var back = JsonSerializer.Deserialize<AppSettings>(json, s_options);

        back.Should().NotBeNull();
        back!.Should().BeEquivalentTo(settings);
    }

    [Fact]
    public void Deserialize_LowercaseKeys_WorksWhenCaseInsensitive()
    {
        // Hand-edited settings.json (or one written by a future build with different casing)
        // should still load — the store sets PropertyNameCaseInsensitive on its options.
        var lowercased = """
            {
              "theme": "dark",
              "fontFamily": "Cascadia Mono",
              "fontSize": 12,
              "httpProxy": null,
              "defaultFollowRedirects": true,
              "defaultVerifySsl": true,
              "defaultSendCookies": true,
              "defaultSaveCookies": true,
              "defaultEncodeUrl": true,
              "requestTimeoutSeconds": 100,
              "themeMode": "light",
              "interfaceZoom": 1.5
            }
            """;

        var parsed = JsonSerializer.Deserialize<AppSettings>(lowercased, s_options);
        parsed.Should().NotBeNull();
        parsed!.ThemeMode.Should().Be("light");
        parsed.InterfaceZoom.Should().Be(1.5);
    }
}
