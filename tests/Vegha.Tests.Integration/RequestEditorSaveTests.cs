using Vegha.App.ViewModels;
using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using Vegha.Core.Requests;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Integration;

public class RequestEditorSaveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RequestEditorViewModel _vm;

    public RequestEditorSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Vegha-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var http = new HttpClient();
        var executor = new HttpExecutor(http);
        var oauth2 = new OAuth2TokenAcquirer(http);
        _vm = new RequestEditorViewModel(executor, oauth2, new Vegha.Core.Scripting.JintHost(), NullLogger<RequestEditorViewModel>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void LoadFromRequestItem_SetsSourcePath_NotDirty()
    {
        var path = Path.Combine(_tempDir, "x.bru");
        File.WriteAllText(path, "stub");

        _vm.LoadFromRequestItem(new RequestItem { Name = "x", Url = "https://x", Method = "GET" }, path);

        _vm.SourcePath.Should().Be(path);
        _vm.IsDirty.Should().BeFalse();
        _vm.SaveCommand.CanExecute(null).Should().BeFalse(); // not dirty
    }

    [Fact]
    public void EditingUrl_MarksDirty_EnablesSave()
    {
        var path = Path.Combine(_tempDir, "x.bru");
        File.WriteAllText(path, "stub");
        _vm.LoadFromRequestItem(new RequestItem { Name = "x", Url = "https://x", Method = "GET" }, path);

        _vm.Url = "https://changed.example.com";

        _vm.IsDirty.Should().BeTrue();
        _vm.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task SaveCommand_WritesEmittedBruToSourcePath_ClearsDirty()
    {
        var path = Path.Combine(_tempDir, "ping.bru");
        File.WriteAllText(path, "(stale)");

        var item = new RequestItem
        {
            Name = "ping",
            Method = "GET",
            Url = "https://example.com/ping",
            Sequence = 1,
            Headers = new List<KvPair> { new("Accept", "application/json") },
        };
        _vm.LoadFromRequestItem(item, path);

        _vm.Url = "https://example.com/pong";
        _vm.IsDirty.Should().BeTrue();

        await _vm.SaveCommand.ExecuteAsync(null);

        _vm.IsDirty.Should().BeFalse();
        var written = await File.ReadAllTextAsync(path);
        written.Should().Contain("name: ping");
        written.Should().Contain("url: https://example.com/pong");
        written.Should().Contain("Accept: application/json");

        // And the written file should round-trip cleanly through our parser+converter.
        var rt = BruToRequestConverter.Convert(BruParser.Parse(written));
        rt.Url.Should().Be("https://example.com/pong");
        rt.Headers.Should().Contain(h => h.Name == "Accept");
    }

    [Fact]
    public async Task Save_AuthFields_AreEmittedAndPersisted()
    {
        var path = Path.Combine(_tempDir, "secured.bru");
        File.WriteAllText(path, "(stale)");

        _vm.LoadFromRequestItem(new RequestItem { Name = "secured", Url = "https://x", Method = "GET" }, path);

        _vm.AuthType = "bearer";
        _vm.BearerToken = "tok-from-ui";
        await _vm.SaveCommand.ExecuteAsync(null);

        var written = await File.ReadAllTextAsync(path);
        written.Should().Contain("auth: bearer");
        written.Should().Contain("auth:bearer {");
        written.Should().Contain("token: tok-from-ui");

        _vm.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void SaveCommand_DisabledWhenSourcePathNull()
    {
        _vm.LoadFromRequestItem(new RequestItem { Name = "x", Url = "https://x", Method = "GET" });
        _vm.Url = "https://changed";
        _vm.IsDirty.Should().BeTrue();
        _vm.SourcePath.Should().BeNull();
        _vm.SaveCommand.CanExecute(null).Should().BeFalse();
    }
}
