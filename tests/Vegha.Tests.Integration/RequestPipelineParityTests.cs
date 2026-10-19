using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>The pipeline features that used to exist only in the request editor's Send:
/// signing auths, Digest retry, multipart / binary bodies, OAuth2 authorization-code, secret
/// resolution, session runtime variables, env deletions and unresolved-placeholder reports.
/// The runner, CLI and VS Code sidecar all send through the pipeline, so these must hold there.</summary>
public class RequestPipelineParityTests : IAsyncLifetime
{
    private WireMockServer _server = null!;
    private HttpClient _client = null!;
    private HttpExecutor _http = null!;
    private readonly JintHost _script = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"vegha-pipe-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        _client = new HttpClient();
        _http = new HttpExecutor(_client);
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop(); _server.Dispose(); _client.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    private string Url(string path) => _server.Urls[0] + path;

    private static RequestPipeline.Inputs Inputs(
        RequestItem request,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyDictionary<string, string>? runtime = null)
    {
        var collection = new Collection { Name = "c", Requests = new List<RequestItem> { request } };
        return new RequestPipeline.Inputs(collection, Array.Empty<Folder>(), request,
            env ?? new Dictionary<string, string>(), new Dictionary<string, string>(),
            RuntimeVariables: runtime);
    }

    private static AuthConfig Auth(AuthType type, params (string Key, string Value)[] p) =>
        new() { Type = type, Parameters = p.ToDictionary(x => x.Key, x => x.Value) };

    private string? SentHeader(string name) =>
        _server.LogEntries.Last().RequestMessage.Headers!.TryGetValue(name, out var v) ? v.Single() : null;

    // ---------------- signing auths ----------------

    [Fact]
    public async Task AwsV4_signs_with_amz_headers()
    {
        _server.Given(Request.Create().WithPath("/s3").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        var req = new RequestItem
        {
            Method = "GET", Url = Url("/s3"),
            Auth = Auth(AuthType.AwsV4, ("accessKeyId", "AKID"), ("secretAccessKey", "{{secret}}"),
                ("region", "us-east-1"), ("service", "s3")),
        };

        var r = await RequestPipeline.ExecuteAsync(Inputs(req, new Dictionary<string, string> { ["secret"] = "sk" }), _http, _script);

        r.StatusCode.Should().Be(200);
        SentHeader("Authorization").Should().StartWith("AWS4-HMAC-SHA256 Credential=AKID/");
        SentHeader("X-Amz-Date").Should().NotBeNullOrEmpty();
        SentHeader("X-Amz-Content-Sha256").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Wsse_adds_username_token_header()
    {
        _server.Given(Request.Create().WithPath("/w").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        var req = new RequestItem
        {
            Method = "GET", Url = Url("/w"),
            Auth = Auth(AuthType.Wsse, ("username", "bob"), ("password", "pw")),
        };

        await RequestPipeline.ExecuteAsync(Inputs(req), _http, _script);

        SentHeader("X-WSSE").Should().StartWith("UsernameToken Username=\"bob\"").And.Contain("PasswordDigest=");
    }

    [Fact]
    public void Wsse_digest_matches_spec_formula()
    {
        var nonce = new byte[] { 1, 2, 3, 4 };
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var header = AuthSigning.BuildWsseHeader("u", "p", now, nonce);

        var created = now.ToString("o");
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(
            nonce.Concat(System.Text.Encoding.UTF8.GetBytes(created + "p")).ToArray()));
        header.Should().Contain($"PasswordDigest=\"{expected}\"").And.Contain($"Nonce=\"{Convert.ToBase64String(nonce)}\"");
    }

    [Fact]
    public async Task Digest_answers_the_401_challenge_once()
    {
        _server.Given(Request.Create().WithPath("/digest").UsingGet()
                .WithHeader("Authorization", "Digest *", WireMock.Matchers.MatchBehaviour.AcceptOnMatch))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("in"));
        _server.Given(Request.Create().WithPath("/digest").UsingGet())
            .AtPriority(2)
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithHeader("WWW-Authenticate", "Digest realm=\"test\", nonce=\"abc123\", qop=\"auth\""));
        var req = new RequestItem
        {
            Method = "GET", Url = Url("/digest"),
            Auth = Auth(AuthType.Digest, ("username", "alice"), ("password", "pw")),
        };

        var r = await RequestPipeline.ExecuteAsync(Inputs(req), _http, _script);

        r.StatusCode.Should().Be(200);
        r.ResponseBody.Should().Be("in");
        _server.LogEntries.Should().HaveCount(2);
        SentHeader("Authorization").Should().Contain("username=\"alice\"").And.Contain("realm=\"test\"");
    }

    [Fact]
    public void Ntlm_credential_is_built_with_domain_and_interpolation()
    {
        var auth = Auth(AuthType.Ntlm, ("username", "{{u}}"), ("password", "pw"), ("domain", "CORP"));
        var cred = AuthSigning.NtlmCredential(auth, new Dictionary<string, string> { ["u"] = "svc" });

        cred.Should().NotBeNull();
        cred!.UserName.Should().Be("svc");
        cred.Domain.Should().Be("CORP");
        AuthSigning.NtlmCredential(Auth(AuthType.Basic), new Dictionary<string, string>()).Should().BeNull();
    }

    // ---------------- bodies ----------------

    [Fact]
    public async Task Multipart_sends_text_and_file_parts_resolving_relative_paths()
    {
        File.WriteAllText(Path.Combine(_tempDir, "upload.txt"), "FILE-CONTENT");
        _server.Given(Request.Create().WithPath("/upload").UsingPost()).RespondWith(Response.Create().WithStatusCode(201));
        var req = new RequestItem
        {
            Method = "POST", Url = Url("/upload"),
            Body = new BodyConfig
            {
                Mode = BodyMode.MultipartForm,
                MultipartItems = new List<MultipartFormItem>
                {
                    new() { Name = "title", Value = "{{t}}" },
                    new() { Name = "doc", Value = "upload.txt", Kind = "file" },
                    new() { Name = "skipped", Value = "x", Enabled = false },
                },
            },
        };

        var r = await RequestPipeline.ExecuteAsync(
            Inputs(req, new Dictionary<string, string> { ["t"] = "hello" }), _http, _script, default,
            new RequestPipeline.Options { BasePath = _tempDir });

        r.StatusCode.Should().Be(201);
        var sent = _server.LogEntries.Single().RequestMessage;
        sent.Headers!["Content-Type"].Single().Should().StartWith("multipart/form-data");
        sent.Body.Should().Contain("hello").And.Contain("FILE-CONTENT").And.NotContain("skipped");
    }

    [Fact]
    public async Task Binary_body_streams_the_file()
    {
        var path = Path.Combine(_tempDir, "blob.bin");
        File.WriteAllBytes(path, new byte[] { 0x42, 0x43, 0x44 });
        _server.Given(Request.Create().WithPath("/bin").UsingPut()).RespondWith(Response.Create().WithStatusCode(204));
        var req = new RequestItem
        {
            Method = "PUT", Url = Url("/bin"),
            Body = new BodyConfig { Mode = BodyMode.Binary, FilePath = path, FileContentType = "application/x-test" },
        };

        var r = await RequestPipeline.ExecuteAsync(Inputs(req), _http, _script);

        r.StatusCode.Should().Be(204);
        var sent = _server.LogEntries.Single().RequestMessage;
        sent.BodyAsBytes.Should().Equal(0x42, 0x43, 0x44);
        sent.Headers!["Content-Type"].Single().Should().StartWith("application/x-test");
    }

    // ---------------- OAuth2 authorization_code ----------------

    [Fact]
    public async Task OAuth2_authorization_code_runs_when_host_allows_interactive_auth()
    {
        var port = FreePort();
        var callback = $"http://127.0.0.1:{port}/cb";
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"ac-tok\",\"expires_in\":3600}"));
        _server.Given(Request.Create().WithPath("/api").UsingGet().WithHeader("Authorization", "Bearer ac-tok"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        var launched = 0;
        var acquirer = new OAuth2TokenAcquirer(new HttpClient())
        {
            BrowserLauncher = authUrl =>
            {
                launched++;
                var state = System.Web.HttpUtility.ParseQueryString(new Uri(authUrl).Query)["state"];
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    using var c = new HttpClient();
                    await c.GetAsync($"{callback}?code=C0DE&state={state}");
                });
                return Task.CompletedTask;
            },
        };
        var req = new RequestItem
        {
            Method = "GET", Url = Url("/api"),
            Auth = Auth(AuthType.OAuth2, ("grant_type", "authorization_code"),
                ("authorization_url", "https://login.test/authorize"),
                ("access_token_url", Url("/oauth/token")), ("callback_url", callback),
                ("client_id", "cid"), ("client_secret", "cs"), ("token_id", Guid.NewGuid().ToString("N"))),
        };

        var r = await RequestPipeline.ExecuteAsync(Inputs(req), _http, _script, default,
            new RequestPipeline.Options { OAuth2 = acquirer, AllowInteractiveAuth = true });

        r.ErrorMessage.Should().BeNull();
        r.StatusCode.Should().Be(200);
        launched.Should().Be(1);
        r.OAuth2Token!.AccessToken.Should().Be("ac-tok");
    }

    // ---------------- variables ----------------

    [Fact]
    public async Task Secrets_resolver_runs_before_interpolation()
    {
        _server.Given(Request.Create().WithPath("/sec").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        var req = new RequestItem
        {
            Method = "GET", Url = Url("/sec"),
            Headers = new List<KvPair> { new("X-Key", "{{apiKey}}") },
        };
        var options = new RequestPipeline.Options
        {
            ResolveSecrets = (vars, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(
                vars.ToDictionary(kv => kv.Key, kv => kv.Value == "secret://vault/key" ? "resolved!" : kv.Value)),
        };

        await RequestPipeline.ExecuteAsync(
            Inputs(req, new Dictionary<string, string> { ["apiKey"] = "secret://vault/key" }), _http, _script, default, options);

        SentHeader("X-Key").Should().Be("resolved!");
    }

    [Fact]
    public async Task Secrets_resolver_failure_is_reported_without_sending()
    {
        var req = new RequestItem { Method = "GET", Url = Url("/never") };
        var options = new RequestPipeline.Options
        {
            ResolveSecrets = (_, _) => throw new InvalidOperationException("vault offline"),
        };

        var r = await RequestPipeline.ExecuteAsync(Inputs(req), _http, _script, default, options);

        r.ErrorMessage.Should().Contain("vault offline");
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Runtime_variables_beat_collection_vars_and_only_new_sets_are_reported()
    {
        _server.Given(Request.Create().WithPath("/rt").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        var req = new RequestItem
        {
            Method = "GET", Url = Url("/rt"),
            Headers = new List<KvPair> { new("X-Who", "{{who}}") },
            PostResponseScript = "bru.setVar('fresh', 'yes');",
        };
        var collection = new Collection
        {
            Name = "c",
            Variables = new List<KvPair> { new("who", "collection") },
            Requests = new List<RequestItem> { req },
        };
        var inputs = new RequestPipeline.Inputs(collection, Array.Empty<Folder>(), req,
            new Dictionary<string, string>(), new Dictionary<string, string>(),
            RuntimeVariables: new Dictionary<string, string> { ["who"] = "runtime", ["kept"] = "1" });

        var r = await RequestPipeline.ExecuteAsync(inputs, _http, _script);

        SentHeader("X-Who").Should().Be("runtime");
        r.RuntimeVariablesSet.Should().ContainKey("fresh").And.NotContainKey("kept").And.NotContainKey("who");
    }

    [Fact]
    public async Task Pre_request_env_changes_and_deletions_are_reported()
    {
        _server.Given(Request.Create().WithPath("/env").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        var req = new RequestItem
        {
            Method = "GET", Url = Url("/env"),
            Headers = new List<KvPair> { new("X-Token", "{{token}}") },
            PreRequestScript = "bru.setEnvVar('token', 'minted'); bru.deleteEnvVar('stale');",
        };

        var r = await RequestPipeline.ExecuteAsync(
            Inputs(req, new Dictionary<string, string> { ["stale"] = "x", ["token"] = "old" }), _http, _script);

        SentHeader("X-Token").Should().Be("minted", "pre-request env writes feed this request's interpolation");
        r.EnvVarMutations.Should().Contain("token", "minted");
        r.EnvVarDeletions.Should().Equal("stale");
    }

    [Fact]
    public async Task Unresolved_placeholders_are_reported()
    {
        _server.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(200));
        var req = new RequestItem
        {
            Method = "POST", Url = Url("/u"),
            Headers = new List<KvPair> { new("X-A", "{{missingHeader}}") },
            Body = new BodyConfig { Mode = BodyMode.Json, Content = "{\"a\":\"{{missingBody}}\"}" },
        };

        var r = await RequestPipeline.ExecuteAsync(Inputs(req), _http, _script);

        r.UnresolvedVariables.Should().Equal("missingHeader", "missingBody");
        r.Response.Should().NotBeNull();
        r.Response!.Timing.Should().NotBeNull();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
