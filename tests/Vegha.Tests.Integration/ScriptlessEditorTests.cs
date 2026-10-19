using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Scriptless;
using Vegha.App.ViewModels.Services;
using Vegha.Core.Engine;
using Vegha.Core.Domain;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using Vegha.Core.Scripting.Testing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>Scriptless captures, assertions and linked requests through the request editor's
/// Send path (the code the desktop app runs), including the shared runtime-variable store.</summary>
public class ScriptlessEditorTests : IAsyncLifetime
{
    private WireMockServer _server = null!;
    private HttpClient _client = null!;
    private readonly RuntimeVariableStore _runtime = new();
    private readonly LastResponseCache _cache = new();

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        _client = new HttpClient();
        _server.Given(Request.Create().WithPath("/login").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("X-Session", "sess-1")
                .WithBody("""{"access_token":"tok-123","user":{"id":7,"email":"ada@x.io"}}"""));
        _server.Given(Request.Create().WithPath("/me").UsingGet().WithHeader("Authorization", "Bearer tok-123"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"id":7,"name":"Ada"}"""));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop(); _server.Dispose(); _client.Dispose();
        return Task.CompletedTask;
    }

    private RequestEditorViewModel NewEditor() => new(
        new HttpExecutor(_client), new OAuth2TokenAcquirer(_client), new JintHost(),
        NullLogger<RequestEditorViewModel>.Instance,
        runtimeStore: _runtime, responseCache: _cache);

    private RequestItem LoginItem(string dir) => new()
    {
        Name = "Login",
        Method = "POST",
        Url = _server.Url + "/login",
        SourcePath = Path.Combine(dir, "Login.bru"),
        PostResponseVars = { new KvPair("token", "res.body.access_token") },
        PostResponseEnvVars = { new KvPair("session", "res.headers['x-session']") },
        Assertions = { new KvPair("res.status", "eq 200"), new KvPair("res.body.user.email", "isEmail") },
    };

    private RequestItem MeItem(string dir, params KvPair[] links) => new()
    {
        Name = "Me",
        Method = "GET",
        Url = _server.Url + "/me",
        SourcePath = Path.Combine(dir, "Me.bru"),
        Headers = { new KvPair("Authorization", "Bearer {{token}}") },
        Assertions = { new KvPair("res.body.name", "eq Ada") },
        ChainLinks = links.ToList(),
    };

    [Fact]
    public async Task Send_applies_captures_shares_runtime_vars_and_reports_assertions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vegha-scriptless-" + Guid.NewGuid().ToString("N"));
        var login = LoginItem(dir);
        var me = MeItem(dir);
        var collection = new Collection { Name = "c", Requests = { login, me } };

        var loginEditor = NewEditor();
        loginEditor.LoadFromRequestItem(login, login.SourcePath);
        loginEditor.SetParentContext(collection, Array.Empty<Folder>());
        EnvVarMutationEventArgs? envMutation = null;
        loginEditor.EnvironmentVariablesMutated += (_, e) => envMutation ??= e;

        await loginEditor.SendCommand.ExecuteAsync(null);

        loginEditor.ErrorMessage.Should().BeNull();
        _runtime.TryGet("token", out var tokenEntry).Should().BeTrue();
        tokenEntry.Value.Should().Be("tok-123");
        tokenEntry.SourceRequest.Should().Be("Login");
        tokenEntry.SourceDetail.Should().Be("res.body.access_token");
        envMutation!.Updated.Should().Contain("session", "sess-1");
        loginEditor.TestResults.Should().HaveCount(2).And.OnlyContain(t => t.Passed && t.IsAssertion);
        loginEditor.Assertions.Where(a => !a.IsBlank).Should().OnlyContain(a => a.IsOk);
        loginEditor.Captures.First(c => c.Name == "token").ResultText.Should().Contain("tok-123");
        loginEditor.IsDirty.Should().BeFalse("results and previews are not edits");

        // A second tab resolves {{token}} from the shared runtime store.
        var meEditor = NewEditor();
        meEditor.LoadFromRequestItem(me, me.SourcePath);
        meEditor.SetParentContext(collection, Array.Empty<Folder>());
        meEditor.ResolvedVariablesSnapshot.Should().Contain("token", "tok-123");
        await meEditor.SendCommand.ExecuteAsync(null);
        meEditor.ResponseStatusCode.Should().Be(200);
        meEditor.TestResults.Should().ContainSingle().Which.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Linked_request_runs_first_when_variable_is_empty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vegha-scriptless-" + Guid.NewGuid().ToString("N"));
        var login = LoginItem(dir);
        var me = MeItem(dir, new KvPair("Login.bru", "when-empty token"));
        var collection = new Collection { Name = "c", Requests = { login, me } };

        var editor = NewEditor();
        editor.LoadFromRequestItem(me, me.SourcePath);
        editor.SetParentContext(collection, Array.Empty<Folder>());

        await editor.SendCommand.ExecuteAsync(null);

        editor.ErrorMessage.Should().BeNull();
        editor.ResponseStatusCode.Should().Be(200);
        editor.ChainSteps.Should().ContainSingle().Which.Ran.Should().BeTrue();
        _runtime.Snapshot().Should().Contain("token", "tok-123");

        // Token now set → the link is skipped on the next send.
        await editor.SendCommand.ExecuteAsync(null);
        editor.ChainSteps.Should().ContainSingle().Which.Ran.Should().BeFalse();
    }

    [Fact]
    public async Task Previews_track_the_last_response_and_generate_seeds_assertions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vegha-scriptless-" + Guid.NewGuid().ToString("N"));
        var login = LoginItem(dir) with { Assertions = new List<KvPair>(), PostResponseVars = new List<KvPair>(), PostResponseEnvVars = new List<KvPair>() };
        var editor = NewEditor();
        editor.LoadFromRequestItem(login, login.SourcePath);
        await editor.SendCommand.ExecuteAsync(null);

        var row = editor.AddCaptureFromResponse("uid", ResponseSource.Body, "user.id");
        row.IsOk.Should().BeTrue();
        row.ResultText.Should().Be("→ 7");
        row.PathSuggestions.Should().Contain(new[] { "access_token", "user.id", "user.email" });

        row.PathText = "user.nope";
        row.IsMissing.Should().BeTrue();

        editor.GenerateAssertionsFromResponseCommand.Execute(null);
        var generated = editor.Assertions.Where(a => !a.IsBlank).ToList();
        generated.Should().Contain(a => a.Source == ResponseSource.Status && a.Expected == "200");
        generated.Should().Contain(a => a.Source == ResponseSource.Header && a.Expected == "application/json");
        generated.Should().Contain(a => a.Source == ResponseSource.Body && a.PathText == "user" && a.Operator.Token == "isObject");
        generated.Should().OnlyContain(a => a.IsOk, "generated assertions pass against the response they came from");

        var saved = editor.BuildRequestItemFromVm();
        saved.PostResponseVars.Should().ContainSingle().Which.Should().Be(new KvPair("uid", "res.body.user.nope"));
        saved.Assertions.Should().Contain(new KvPair("res.status", "eq 200"));
    }

    [Fact]
    public void Chain_tab_offers_other_requests_shows_inherited_links_and_opens_a_linked_request()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vegha-chain-" + Guid.NewGuid().ToString("N"));
        var login = LoginItem(dir);
        var me = MeItem(dir, new KvPair("Login.bru", "when-empty token"));
        var collection = new Collection { Name = "c", Requests = { login, me }, ChainLinks = { new KvPair("Login.bru", "when-stale 1h") } };

        var editor = NewEditor();
        editor.LoadFromRequestItem(me, me.SourcePath);
        editor.SetParentContext(collection, Array.Empty<Folder>());
        var chain = editor.ChainEditor;

        chain.Candidates.Select(c => c.Name).Should().Equal(new[] { "Login" }, "the request itself can't be linked");
        chain.Inherited.Should().ContainSingle().Which.Should().Be(
            new InheritedChainLink("collection", "POST", "Login", "when the last run is older than 1h", true));
        var row = editor.ChainLinks[0];
        row.Target!.Name.Should().Be("Login");
        row.VariableName.Should().Be("token");

        LinkedRequestOpenEventArgs? opened = null;
        editor.OpenLinkedRequestRequested += (_, e) => opened = e;
        chain.OpenCommand.Execute(row);
        opened!.Request.Should().BeSameAs(login);

        editor.IsDirty.Should().BeFalse("resolving the rows is not an edit");
        row.ModeOption = ScriptlessOptions.ChainModeFor(ChainRunMode.Always);
        editor.IsDirty.Should().BeTrue();
    }
}
