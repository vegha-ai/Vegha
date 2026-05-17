using System.Reflection;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

public class LayoutSettingsStoreTests : IDisposable
{
    private readonly string _backupPath;
    private readonly string _userDir;
    private readonly bool _hadOriginal;

    public LayoutSettingsStoreTests()
    {
        // Tests share LocalAppData with the user, so back up any pre-existing layout.json.
        _userDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vegha");
        var layoutFile = Path.Combine(_userDir, "layout.json");
        _backupPath = layoutFile + ".test-backup";
        _hadOriginal = File.Exists(layoutFile);
        if (_hadOriginal) File.Move(layoutFile, _backupPath, overwrite: true);
        else if (Directory.Exists(_userDir) && File.Exists(layoutFile)) File.Delete(layoutFile);
    }

    public void Dispose()
    {
        var layoutFile = Path.Combine(_userDir, "layout.json");
        if (File.Exists(layoutFile)) File.Delete(layoutFile);
        if (_hadOriginal && File.Exists(_backupPath)) File.Move(_backupPath, layoutFile, overwrite: true);
    }

    [Fact]
    public void LoadReturnsDefaultsWhenNoFile()
    {
        var store = new LayoutSettingsStore();
        var loaded = store.Load();
        loaded.Should().Be(LayoutSettings.Default);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var store = new LayoutSettingsStore();
        var settings = new LayoutSettings(SidebarWidth: 350, RightPanelWidth: 410, ResponsePaneHeight: 480);
        store.Save(settings);

        var loaded = store.Load();
        loaded.Should().Be(settings);
    }

    [Fact]
    public void SaveClampsValuesToConstraintBounds()
    {
        var store = new LayoutSettingsStore();
        // Values outside the allowed ranges (sidebar 200-480, right 220-520, response 160-640).
        store.Save(new LayoutSettings(SidebarWidth: 50, RightPanelWidth: 9999, ResponsePaneHeight: -10));

        var loaded = store.Load();
        loaded.SidebarWidth.Should().Be(200);
        loaded.RightPanelWidth.Should().Be(520);
        loaded.ResponsePaneHeight.Should().Be(160);
    }

    [Fact]
    public void LoadReturnsDefaultsForCorruptJson()
    {
        var layoutFile = Path.Combine(_userDir, "layout.json");
        Directory.CreateDirectory(_userDir);
        File.WriteAllText(layoutFile, "{ this is not valid json");

        var store = new LayoutSettingsStore();
        var loaded = store.Load();
        loaded.Should().Be(LayoutSettings.Default);
    }
}
