using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

public class AppSettingsStoreTests
{
    [Fact]
    public void DefaultsAreApplied_WhenNoFileExists()
    {
        // The store reads/writes %LocalAppData%/Vegha/settings.json — touching that path on disk
        // is fine for an integration-style test, but we'll verify just the in-memory contract here.
        var d = AppSettings.Default;
        d.Theme.Should().Be("dark");
        d.RequestTimeoutSeconds.Should().Be(100);
        d.DefaultFollowRedirects.Should().BeTrue();
        d.DefaultVerifySsl.Should().BeTrue();
    }

    [Fact]
    public void Save_Then_Load_RoundTrips_AllFields()
    {
        var store = new AppSettingsStore();
        var snapshot = store.Load(); // capture current state to restore later
        try
        {
            var custom = new AppSettings(
                Theme: "light",
                FontFamily: "Cascadia Code",
                FontSize: 14,
                HttpProxy: "http://proxy.test:3128",
                DefaultFollowRedirects: false,
                DefaultVerifySsl: false,
                DefaultSendCookies: false,
                DefaultSaveCookies: false,
                DefaultEncodeUrl: false,
                RequestTimeoutSeconds: 30);

            store.Save(custom);
            var loaded = store.Load();

            loaded.Should().BeEquivalentTo(custom);
        }
        finally
        {
            store.Save(snapshot);
        }
    }
}
