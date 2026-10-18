using FluentAssertions;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Scriptless;
using Vegha.App.ViewModels.Services;
using Vegha.Core.Engine;
using Vegha.Core.Domain;
using Vegha.Core.Scripting.Testing;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Folder / collection settings must carry the scriptless blocks through a save —
/// the snapshot is emitted verbatim to collection.bru / folder.bru, so anything it drops is lost.</summary>
public class ScriptlessNodePropertiesTests
{
    [Fact]
    public void Collection_snapshot_keeps_assertions_and_chain_links()
    {
        var login = new RequestItem { Name = "Login" };
        var collection = new Collection
        {
            Name = "C",
            Requests = { login },
            Assertions = { new KvPair("res.status", "lt 500") },
            ChainLinks = { new KvPair("Login.bru", "when-empty token") },
        };
        var vm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Collection, collection);

        vm.Assertions.Should().HaveCount(2, "one row plus the ghost row");
        vm.LinkableRequestPaths.Should().Equal("Login.bru");

        // Typing into the ghost row spawns a new ghost — hold the reference.
        var row = vm.Assertions[^1];
        row.Source = ResponseSource.ResponseTime;
        row.Operator = AssertionOperators.Find("lt")!;
        row.Expected = "2000";
        vm.Assertions[^1].IsBlank.Should().BeTrue("a fresh ghost row follows the edited one");

        var snap = vm.BuildSnapshot().Collection!;
        snap.Assertions.Should().Equal(new KvPair("res.status", "lt 500"), new KvPair("res.responseTime", "lt 2000"));
        snap.ChainLinks.Should().Equal(new KvPair("Login.bru", "when-empty token"));
    }

    [Fact]
    public void Folder_snapshot_keeps_assertions_and_chain_links()
    {
        var folder = new Folder
        {
            Name = "users",
            Assertions = { new KvPair("res.body", "isJson", enabled: false) },
            ChainLinks = { new KvPair("auth/Login.bru", "always") },
        };
        var vm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Folder, folder);
        var snap = vm.BuildSnapshot().Folder!;
        snap.Assertions.Should().Equal(new KvPair("res.body", "isJson", enabled: false));
        snap.ChainLinks.Should().Equal(new KvPair("auth/Login.bru", "always"));
    }

    [Fact]
    public void Editor_variable_snapshot_includes_collection_and_folder_vars()
    {
        var http = new HttpClient();
        var vm = new RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(http), new Vegha.Core.Requests.OAuth2TokenAcquirer(http),
            new Vegha.Core.Scripting.JintHost(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RequestEditorViewModel>.Instance);
        var raised = 0;
        vm.VariablesSnapshotChanged += (_, _) => raised++;

        var folder = new Folder { Name = "f", Variables = { new KvPair("shared", "from-folder"), new KvPair("f", "1") } };
        vm.SetParentContext(
            new Collection { Name = "c", Variables = { new KvPair("baseUrl", "https://x"), new KvPair("shared", "from-collection") } },
            new[] { folder });

        raised.Should().BeGreaterThan(0, "{{var}} highlighting must refresh when the parent context arrives");
        vm.ResolvedVariablesSnapshot.Should().Contain("baseUrl", "https://x")
            .And.Contain("shared", "from-folder")
            .And.Contain("f", "1");
    }

    [Fact]
    public void Runtime_variables_view_tracks_the_store()
    {
        var store = new RuntimeVariableStore();
        string? promoted = null;
        var vm = new RuntimeVariablesViewModel(store, (n, v) => promoted = n + "=" + v);
        vm.HasRows.Should().BeFalse();

        store.Set("token", "abc", "Login", "res.body.access_token");
        vm.Rows.Should().ContainSingle().Which.Provenance.Should().StartWith("captured from Login · res.body.access_token");

        vm.PromoteCommand.Execute(vm.Rows[0]);
        promoted.Should().Be("token=abc");
        store.Count.Should().Be(0, "promoting moves the value into the environment");

        store.Set("a", "1");
        vm.ClearCommand.Execute(null);
        vm.HasRows.Should().BeFalse();
    }
}
