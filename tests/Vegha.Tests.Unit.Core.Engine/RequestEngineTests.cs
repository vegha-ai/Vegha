using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Engine;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Unit.Core.Engine;

public class RequestEngineTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"vegha-engine-{Guid.NewGuid():N}");
    private readonly EngineServices _services;
    private readonly RequestEngine _engine;

    public RequestEngineTests()
    {
        _services = new EngineServices(new EngineOptions
        {
            ClientName = "vscode",
            DataDirectory = _dataDir,
            ApplyUserSettings = false, // never read the developer's real settings.json
        });
        _engine = new RequestEngine(_services);
    }

    public void Dispose()
    {
        _services.Dispose();
        _server.Stop();
        _server.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }

    private SendContext Context(RequestItem request, IReadOnlyDictionary<string, string>? env = null) =>
        new(new Collection { Name = "c", Requests = new List<RequestItem> { request } },
            Array.Empty<Folder>(), request, env ?? new Dictionary<string, string>())
        {
            HistoryWorkspaceId = "/ws",
        };

    [Fact]
    public async Task Send_records_history_with_client_tag_and_replay_blob()
    {
        _server.Given(Request.Create().WithPath("/h").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody("hi"));
        var request = new RequestItem { Name = "Hello", Method = "GET", Url = _server.Urls[0] + "/h" };

        var result = await _engine.SendAsync(Context(request));

        result.Outputs.StatusCode.Should().Be(200);
        result.HistoryId.Should().NotBeNull();
        var rows = await _services.History!.GetRecentAsync(limit: 10, workspaceId: "/ws");
        rows.Should().ContainSingle(r => r.Url == _server.Urls[0] + "/h" && r.StatusCode == 200);
        var blob = await _services.History.GetRequestBlobAsync(result.HistoryId!.Value);
        blob.Should().Contain("\"Name\":\"Hello\"");

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_services.History.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client FROM history";
        cmd.ExecuteScalar().Should().Be("vscode");
    }

    [Fact]
    public async Task Runtime_variables_set_by_one_send_feed_the_next()
    {
        _server.Given(Request.Create().WithPath("/login").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"token\":\"t-123\"}"));
        _server.Given(Request.Create().WithPath("/me").UsingGet().WithHeader("Authorization", "Bearer t-123"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("me"));

        var login = new RequestItem
        {
            Name = "Login", Method = "POST", Url = _server.Urls[0] + "/login",
            PostResponseScript = "bru.setVar('token', res.getBody().token);",
        };
        var me = new RequestItem
        {
            Name = "Me", Method = "GET", Url = _server.Urls[0] + "/me",
            Auth = new AuthConfig { Type = AuthType.Bearer, Parameters = new Dictionary<string, string> { ["token"] = "{{token}}" } },
        };

        await _engine.SendAsync(Context(login));
        _services.RuntimeVariables.TryGet("token", out var entry).Should().BeTrue();
        entry.SourceRequest.Should().Be("Login");
        entry.Value.Should().Be("t-123");

        var result = await _engine.SendAsync(Context(me));
        result.Outputs.StatusCode.Should().Be(200);
        result.Outputs.ResponseBody.Should().Be("me");
    }

    [Fact]
    public async Task RecordHistory_false_skips_history()
    {
        _server.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(204));
        var request = new RequestItem { Method = "GET", Url = _server.Urls[0] + "/quiet" };

        var result = await _engine.SendAsync(Context(request) with { RecordHistory = false });

        result.HistoryId.Should().BeNull();
        (await _services.History!.CountAsync("/ws")).Should().Be(0);
    }

    [Fact]
    public async Task Transport_failure_is_reported_and_not_recorded()
    {
        var request = new RequestItem { Method = "GET", Url = "http://127.0.0.1:1/unreachable" };

        var result = await _engine.SendAsync(Context(request));

        result.Outputs.ErrorMessage.Should().NotBeNullOrEmpty();
        result.Outputs.StatusCode.Should().Be(0);
    }
}
