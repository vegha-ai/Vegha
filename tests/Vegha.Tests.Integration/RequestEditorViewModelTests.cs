using Vegha.App.ViewModels;
using Vegha.Core.Requests;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Vegha.Tests.Integration;

public class RequestEditorViewModelTests : IAsyncLifetime
{
    private WireMockServer _server = null!;
    private RequestEditorViewModel _vm = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        _client = new HttpClient();
        var executor = new HttpExecutor(_client);
        var oauth2 = new OAuth2TokenAcquirer(_client);
        _vm = new RequestEditorViewModel(executor, oauth2, new Vegha.Core.Scripting.JintHost(), NullLogger<RequestEditorViewModel>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop();
        _server.Dispose();
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SendCommand_PopulatesResponseState()
    {
        _server.Given(Request.Create().WithPath("/users/42").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("X-Custom", "value")
                .WithBody("hello"));

        _vm.Method = "GET";
        _vm.Url = $"{_server.Url}/users/42";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.HasResponse.Should().BeTrue();
        _vm.ResponseStatusCode.Should().Be(200);
        _vm.ResponseBody.Should().Be("hello");
        _vm.ErrorMessage.Should().BeNull();
        _vm.ResponseHeaders.Should().Contain(h => h.Name == "X-Custom" && h.Value == "value");
        _vm.IsSending.Should().BeFalse();
    }

    [Fact]
    public async Task RawResponseText_ContainsStatusLine_Headers_AndBody()
    {
        _server.Given(Request.Create().WithPath("/raw").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("X-Trace", "abc")
                .WithBody("body-here"));

        _vm.Url = $"{_server.Url}/raw";
        await _vm.SendCommand.ExecuteAsync(null);

        _vm.RawResponseText.Should().StartWith("HTTP/1.1 200");
        _vm.RawResponseText.Should().Contain("X-Trace: abc");
        _vm.RawResponseText.Should().EndWith("body-here");
    }

    [Fact]
    public async Task SendCommand_InvalidUrl_SetsErrorMessage()
    {
        _vm.Url = "not a valid url";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.HasResponse.Should().BeFalse();
        _vm.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SendCommand_TransportError_SurfacesError()
    {
        _vm.Method = "GET";
        _vm.Url = "http://127.0.0.1:1/never-listens-here";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.HasResponse.Should().BeTrue(); // result was returned (with error)
        _vm.ErrorMessage.Should().NotBeNullOrEmpty();
        _vm.ResponseStatusCode.Should().Be(0);
    }

    [Fact]
    public void SendCommand_DisabledWhenUrlEmpty()
    {
        _vm.Url = "";
        _vm.SendCommand.CanExecute(null).Should().BeFalse();

        _vm.Url = "https://example.com";
        _vm.SendCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task SendCommand_AppendsEnabledQueryParams()
    {
        _server.Given(Request.Create()
                .WithPath("/search")
                .WithParam("q", "hello")
                .WithParam("limit", "5")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        _vm.Url = $"{_server.Url}/search";
        _vm.Params.Add(new Vegha.App.ViewModels.KvEntry("q", "hello"));
        _vm.Params.Add(new Vegha.App.ViewModels.KvEntry("limit", "5"));
        _vm.Params.Add(new Vegha.App.ViewModels.KvEntry("disabled", "x", enabled: false));
        _vm.Params.Add(new Vegha.App.ViewModels.KvEntry("", "no-name"));

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_SendsEnabledHeaders()
    {
        _server.Given(Request.Create()
                .WithPath("/protected")
                .WithHeader("X-Tenant", "acme")
                .WithHeader("Authorization", "Bearer abc")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        _vm.Url = $"{_server.Url}/protected";
        _vm.Headers.Add(new Vegha.App.ViewModels.KvEntry("X-Tenant", "acme"));
        _vm.Headers.Add(new Vegha.App.ViewModels.KvEntry("Authorization", "Bearer abc"));
        _vm.Headers.Add(new Vegha.App.ViewModels.KvEntry("X-Disabled", "no", enabled: false));

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_SendsJsonBody_WithContentType()
    {
        _server.Given(Request.Create()
                .WithPath("/echo")
                .UsingPost()
                .WithHeader("Content-Type", "application/json; charset=utf-8")
                .WithBody("{\"x\":1}"))
            .RespondWith(Response.Create().WithStatusCode(201));

        _vm.Method = "POST";
        _vm.Url = $"{_server.Url}/echo";
        _vm.BodyType = "json";
        _vm.BodyContent = "{\"x\":1}";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(201);
    }

    [Fact]
    public void ComposeUrl_HandlesExistingQueryString()
    {
        var ent = new[] { new Vegha.App.ViewModels.KvEntry("b", "2") };
        var url = Vegha.App.ViewModels.RequestEditorViewModel
            .ComposeUrl("https://x.test/y?a=1", ent);
        url.Should().Be("https://x.test/y?a=1&b=2");
    }

    [Fact]
    public void ComposeBody_NoneType_ReturnsNull()
    {
        var (body, ct) = Vegha.App.ViewModels.RequestEditorViewModel
            .ComposeBody("none", "ignored");
        body.Should().BeNull();
        ct.Should().BeNull();
    }

    [Fact]
    public async Task SendCommand_TestsBlock_CapturesPassAndFail()
    {
        _server.Given(Request.Create().WithPath("/users/42").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("X-Trace", "abc")
                .WithBody("hello"));

        _vm.Url = $"{_server.Url}/users/42";
        _vm.TestsScript = """
            test('status is 200', function() { expect(res.status).toBe(200); });
            test('body has hello', function() { expect(res.body).toContain('hello'); });
            test('intentional fail', function() { expect(res.status).toBe(999); });
            """;

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.HasResponse.Should().BeTrue();
        _vm.TestResults.Should().HaveCount(3);
        _vm.TestResults.Count(t => t.Passed).Should().Be(2);
        _vm.TestResults.Count(t => !t.Passed).Should().Be(1);
        _vm.TestResults.Last().FailureMessage.Should().Contain("999");
    }

    [Fact]
    public async Task SendCommand_PostResponseScript_SetEnvVar_RaisesEnvVarMutation()
    {
        // Regression: a post-response script that extracts a token via bru.setEnvVar must surface
        // the change so the host can apply it to the active environment — otherwise the next
        // request resolves {{access_token}} as unset.
        _server.Given(Request.Create().WithPath("/token").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("{\"access_token\":\"zVd7kPHFs2y2AwDx17e2TEa4ATDA\"}"));

        Vegha.App.ViewModels.EnvVarMutationEventArgs? captured = null;
        _vm.EnvironmentVariablesMutated += (_, e) => captured = e;

        _vm.Method = "POST";
        _vm.Url = $"{_server.Url}/token";
        _vm.PostResponseScript = """
            var jsonData = JSON.parse(res.getBodyAsText());
            if (jsonData) {
                bru.setEnvVar("access_token", jsonData.access_token);
            }
            """;

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
        captured.Should().NotBeNull();
        captured!.Updated.Should().ContainKey("access_token")
            .WhoseValue.Should().Be("zVd7kPHFs2y2AwDx17e2TEa4ATDA");
        captured.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task SendCommand_PreRequestScript_CanSetVarUsedInUrl()
    {
        _server.Given(Request.Create()
                .WithPath("/users/42")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        _vm.PreRequestScript = "bru.setVar('userId', '42');";
        _vm.Url = $"{_server.Url}/users/{{{{userId}}}}";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
        _vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task SendCommand_PreRequestScript_CanReadEnvVarAndSetRuntimeVar()
    {
        _server.Given(Request.Create()
                .WithPath("/")
                .WithHeader("Authorization", "Bearer fresh-token")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        _vm.EnvironmentVariables = new Dictionary<string, string>
        {
            ["base_token"] = "fresh-token"
        };
        _vm.PreRequestScript = """
            // Pull from env, transform, expose to interpolation.
            var t = bru.getEnvVar('base_token');
            bru.setVar('jwt', t);
            """;
        _vm.Url = _server.Url!;
        _vm.Headers.Add(new Vegha.App.ViewModels.KvEntry("Authorization", "Bearer {{jwt}}"));

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_PreRequestScriptFailure_ShortCircuitsBeforeRequest()
    {
        // No WireMock matcher configured — if request goes out, this would 404.
        // Script throws, so request must NOT go out.
        _vm.PreRequestScript = "throw new Error('boom');";
        _vm.Url = $"{_server.Url}/anything";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ErrorMessage.Should().NotBeNullOrEmpty();
        _vm.ErrorMessage.Should().Contain("boom");
        _vm.HasResponse.Should().BeFalse();
    }

    [Fact]
    public async Task SendCommand_BearerAuth_AddsAuthorizationHeader()
    {
        _server.Given(Request.Create()
                .WithPath("/secure")
                .WithHeader("Authorization", "Bearer abc123")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        _vm.Url = $"{_server.Url}/secure";
        _vm.AuthType = "bearer";
        _vm.BearerToken = "abc123";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_BasicAuth_AddsBase64AuthorizationHeader()
    {
        _server.Given(Request.Create()
                .WithPath("/secure")
                .WithHeader("Authorization", "Basic YWxpY2U6czNjcmV0")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        _vm.Url = $"{_server.Url}/secure";
        _vm.AuthType = "basic";
        _vm.BasicUsername = "alice";
        _vm.BasicPassword = "s3cret";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_GraphQL_PostsJsonBodyWithQueryAndVariables()
    {
        _server.Given(Request.Create()
                .WithPath("/graphql")
                .UsingPost()
                .WithHeader("Content-Type", "application/json; charset=utf-8")
                .WithBody(b => b!.Contains("\"query\":\"{ hello }\"") && b.Contains("\"variables\":{\"limit\":5}")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"data\":{}}"));

        _vm.Method = "POST";
        _vm.Url = $"{_server.Url}/graphql";
        _vm.BodyType = "graphql";
        _vm.GraphQLQuery = "{ hello }";
        _vm.GraphQLVariables = "{\"limit\":5}";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_GraphQL_EmptyVariables_DefaultsToEmptyObject()
    {
        _server.Given(Request.Create()
                .WithPath("/graphql")
                .UsingPost()
                .WithBody(b => b!.Contains("\"variables\":{}")))
            .RespondWith(Response.Create().WithStatusCode(200));

        _vm.Method = "POST";
        _vm.Url = $"{_server.Url}/graphql";
        _vm.BodyType = "graphql";
        _vm.GraphQLQuery = "{ ping }";
        _vm.GraphQLVariables = "";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_GraphQL_InterpolatesVariablesInQueryAndVars()
    {
        _server.Given(Request.Create()
                .WithPath("/graphql")
                .UsingPost()
                .WithBody(b => b!.Contains("\"query\":\"{ user(id: 42) { id } }\"") && b.Contains("\"variables\":{\"x\":\"resolved\"}")))
            .RespondWith(Response.Create().WithStatusCode(200));

        _vm.EnvironmentVariables = new Dictionary<string, string>
        {
            ["userId"] = "42",
            ["scope"] = "resolved",
        };
        _vm.Method = "POST";
        _vm.Url = $"{_server.Url}/graphql";
        _vm.BodyType = "graphql";
        _vm.GraphQLQuery = "{ user(id: {{userId}}) { id } }";
        _vm.GraphQLVariables = "{\"x\":\"{{scope}}\"}";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task IntrospectCommand_PublishesSchemaModel_AndExplorer()
    {
        _server.Given(Request.Create().WithPath("/graphql").UsingPost()
                .WithBody(b => b!.Contains("__schema")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""
                { "data": { "__schema": {
                  "queryType": { "name": "Query" },
                  "types": [ { "kind": "OBJECT", "name": "Query",
                    "fields": [ { "name": "ping", "type": { "kind": "SCALAR", "name": "String" }, "args": [] } ] } ]
                } } }
                """));

        _vm.BodyType = "graphql";
        _vm.Url = $"{_server.Url}/graphql";
        await _vm.IntrospectGraphQLCommand.ExecuteAsync(null);

        _vm.GraphQLSchemaLoaded.Should().BeTrue();
        _vm.GraphQLSchemaModel.Should().NotBeNull();
        _vm.GraphQLSchemaModel!.QueryTypeName.Should().Be("Query");
        _vm.SchemaExplorer.Rows.Should().Contain(r => r.Title == "query: Query");
        _vm.GetSchemaSdl().Should().Contain("type Query");
    }

    [Fact]
    public async Task IntrospectCommand_Disabled_SetsCalmHint_NoSchema()
    {
        _server.Given(Request.Create().WithPath("/graphql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{ "errors": [ { "message": "introspection is disabled" } ] }"""));

        _vm.BodyType = "graphql";
        _vm.Url = $"{_server.Url}/graphql";
        await _vm.IntrospectGraphQLCommand.ExecuteAsync(null);

        _vm.GraphQLSchemaLoaded.Should().BeFalse();
        _vm.GraphQLSchemaHint.Should().Contain("introspection is disabled");
    }

    [Fact]
    public async Task SendCommand_GraphQL_MultiOperation_SendsSelectedOperationName()
    {
        _server.Given(Request.Create()
                .WithPath("/graphql")
                .UsingPost()
                .WithBody(b => b!.Contains("\"operationName\":\"Second\"")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"data\":{}}"));

        _vm.Method = "POST";
        _vm.Url = $"{_server.Url}/graphql";
        _vm.BodyType = "graphql";
        _vm.GraphQLQuery = "query First { a } mutation Second { b }";
        _vm.SelectedGraphQLOperationName = "Second";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_GraphQL_SingleOperation_OmitsOperationName()
    {
        _server.Given(Request.Create()
                .WithPath("/graphql")
                .UsingPost()
                .WithBody(b => b!.Contains("\"query\":") && !b.Contains("operationName")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"data\":{}}"));

        _vm.Method = "POST";
        _vm.Url = $"{_server.Url}/graphql";
        _vm.BodyType = "graphql";
        _vm.GraphQLQuery = "query Only { a }";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task GraphQLOperationPicker_PopulatesFromDocument_AfterDebounce()
    {
        _vm.BodyType = "graphql";
        _vm.GraphQLQuery = "query First { a } mutation Second { b }";

        // Analysis is debounced (250 ms) and marshaled back — poll briefly.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_vm.GraphQLOperationNames.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        _vm.GraphQLOperationNames.Should().Equal("First", "Second");
        _vm.HasMultipleGraphQLOperations.Should().BeTrue();
        _vm.SelectedGraphQLOperationName.Should().Be("First");
    }

    [Fact]
    public void GenerateGraphQLVariables_FillsSkeleton_ForDeclaredVariables()
    {
        _vm.BodyType = "graphql";
        _vm.GraphQLQuery = "query Q($id: ID!, $limit: Int) { node(id: $id) { id } }";
        _vm.GraphQLVariables = "";

        _vm.GenerateGraphQLVariablesCommand.Execute(null);

        var parsed = System.Text.Json.JsonDocument.Parse(_vm.GraphQLVariables).RootElement;
        parsed.GetProperty("id").GetString().Should().Be("");
        parsed.GetProperty("limit").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Prettify_GraphQL_FormatsQueryAndVariables_LeavesBrokenQueryAlone()
    {
        _vm.BodyType = "graphql";
        _vm.GraphQLQuery = "query Q($id:ID!){user(id:$id){id email}}";
        _vm.GraphQLVariables = "{\"id\":\"u1\"}";

        _vm.PrettifyCommand.Execute(null);

        _vm.GraphQLQuery.Should().Contain("query Q($id: ID!)");
        _vm.GraphQLQuery.Split('\n').Length.Should().BeGreaterThan(2);
        _vm.GraphQLVariables.Should().Contain("\"id\": \"u1\"");

        var broken = "query { user { id ";
        _vm.GraphQLQuery = broken;
        _vm.PrettifyCommand.Execute(null);
        _vm.GraphQLQuery.Should().Be(broken);
    }

    [Fact]
    public async Task SendCommand_AwsV4_SignsRequestAndAttachesAwsHeaders()
    {
        // We can't easily verify the signature math against a live AWS endpoint, but we can
        // verify the structural headers ride along: Authorization (with AWS4-HMAC-SHA256),
        // X-Amz-Date, X-Amz-Content-Sha256.
        _server.Given(Request.Create()
                .WithPath("/api/data")
                .WithHeader("Authorization", new WireMock.Matchers.WildcardMatcher("AWS4-HMAC-SHA256*"))
                .WithHeader("X-Amz-Date", new WireMock.Matchers.RegexMatcher(@"^\d{8}T\d{6}Z$"))
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("signed"));

        _vm.Url = $"{_server.Url}/api/data";
        _vm.AuthType = "awsv4";
        _vm.AwsAccessKeyId = "AKIDEXAMPLE";
        _vm.AwsSecretAccessKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
        _vm.AwsRegion = "us-east-1";
        _vm.AwsService = "execute-api";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
        _vm.ResponseBody.Should().Be("signed");
    }

    [Fact]
    public async Task SendCommand_FollowRedirectsOff_DoesNotFollowLocation()
    {
        // 302 → /redirected (which would 200) — but FollowRedirects=off keeps the 302.
        _server.Given(Request.Create().WithPath("/start").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(302)
                .WithHeader("Location", $"{_server.Url}/redirected"));
        _server.Given(Request.Create().WithPath("/redirected").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("dest"));

        _vm.Url = $"{_server.Url}/start";
        _vm.SettingFollowRedirects = false;

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(302); // not followed
    }

    [Fact]
    public async Task SendCommand_FollowRedirectsOn_FollowsToFinalDestination()
    {
        _server.Given(Request.Create().WithPath("/start").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(302)
                .WithHeader("Location", $"{_server.Url}/redirected"));
        _server.Given(Request.Create().WithPath("/redirected").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("dest"));

        _vm.Url = $"{_server.Url}/start";
        _vm.SettingFollowRedirects = true; // default

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
        _vm.ResponseBody.Should().Be("dest");
    }

    [Fact]
    public async Task SendCommand_OAuth2ClientCredentials_AcquiresTokenAndAttachesAsBearer()
    {
        _server.Given(Request.Create()
                .WithPath("/oauth/token")
                .UsingPost()
                .WithBody(b => b!.Contains("grant_type=client_credentials")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("{\"access_token\":\"acquired-token\",\"expires_in\":3600}"));
        _server.Given(Request.Create()
                .WithPath("/api/data")
                .WithHeader("Authorization", "Bearer acquired-token")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        _vm.Url = $"{_server.Url}/api/data";
        _vm.AuthType = "oauth2";
        _vm.OAuth2GrantType = "client_credentials";
        _vm.OAuth2TokenUrl = $"{_server.Url}/oauth/token";
        _vm.OAuth2ClientId = "cid";
        _vm.OAuth2ClientSecret = "csec";
        _vm.OAuth2CredentialsPlacement = "body";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
        _vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task SendCommand_OAuth2_TokenEndpointFails_SurfacesError_NoRequestSent()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(401)
                .WithBody("{\"error\":\"invalid_client\"}"));
        // No matcher for /api/data — if it were sent it'd 404. We assert it was NOT sent.

        _vm.Url = $"{_server.Url}/api/data";
        _vm.AuthType = "oauth2";
        _vm.OAuth2TokenUrl = $"{_server.Url}/oauth/token";
        _vm.OAuth2ClientId = "cid";
        _vm.OAuth2ClientSecret = "csec";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.HasResponse.Should().BeFalse();
        _vm.ErrorMessage.Should().Contain("invalid_client");
    }

    [Fact]
    public async Task SendCommand_ApiKeyHeader_AddsConfiguredHeader()
    {
        _server.Given(Request.Create()
                .WithPath("/secure")
                .WithHeader("X-API-Key", "secret")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        _vm.Url = $"{_server.Url}/secure";
        _vm.AuthType = "apikey";
        _vm.ApiKeyName = "X-API-Key";
        _vm.ApiKeyValue = "secret";
        _vm.ApiKeyPlacement = "header";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_ApiKeyQueryParams_AppendsToUrl()
    {
        _server.Given(Request.Create()
                .WithPath("/secure")
                .WithParam("api_key", "secret")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        _vm.Url = $"{_server.Url}/secure";
        _vm.AuthType = "apikey";
        _vm.ApiKeyName = "api_key";
        _vm.ApiKeyValue = "secret";
        _vm.ApiKeyPlacement = "queryparams";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_BearerToken_InterpolatesPlaceholderFromEnv()
    {
        _server.Given(Request.Create()
                .WithPath("/secure")
                .WithHeader("Authorization", "Bearer from-env")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        _vm.EnvironmentVariables = new Dictionary<string, string> { ["jwt"] = "from-env" };
        _vm.Url = $"{_server.Url}/secure";
        _vm.AuthType = "bearer";
        _vm.BearerToken = "{{jwt}}";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_EnvironmentVariablesUsed_RequestVarsWinOnConflict()
    {
        _server.Given(Request.Create()
                .WithPath("/from/env")
                .WithHeader("X-Tenant", "request-wins")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        // Env supplies baseUrl + a "tenant" var, but request-level Variables override "tenant".
        _vm.EnvironmentVariables = new Dictionary<string, string>
        {
            ["baseUrl"] = _server.Url!,
            ["tenant"] = "env-loses",
        };
        _vm.Url = "{{baseUrl}}/from/env";
        _vm.Variables.Add(new Vegha.App.ViewModels.KvEntry("tenant", "request-wins"));
        _vm.Headers.Add(new Vegha.App.ViewModels.KvEntry("X-Tenant", "{{tenant}}"));

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_ResolvesVariablesInUrlHeadersBody()
    {
        _server.Given(Request.Create()
                .WithPath("/api/v1/users/42")
                .UsingPost()
                .WithHeader("X-Tenant", "acme")
                .WithBody("{\"id\":\"42\"}"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        _vm.Method = "POST";
        _vm.Url = $"{_server.Url}/api/v1/users/{{{{userId}}}}";
        _vm.Variables.Add(new Vegha.App.ViewModels.KvEntry("userId", "42"));
        _vm.Variables.Add(new Vegha.App.ViewModels.KvEntry("tenant", "acme"));
        _vm.Headers.Add(new Vegha.App.ViewModels.KvEntry("X-Tenant", "{{tenant}}"));
        _vm.BodyType = "json";
        _vm.BodyContent = "{\"id\":\"{{userId}}\"}";

        await _vm.SendCommand.ExecuteAsync(null);

        _vm.ResponseStatusCode.Should().Be(200);
    }
}
