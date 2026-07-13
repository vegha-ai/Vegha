using Vegha.Core.Codegen;
using Vegha.Core.Domain;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Codegen;

public class CodegenEmitterTests
{
    private static RequestItem SimpleGet() => new()
    {
        Method = "GET",
        Url = "https://api.acme.io/v1/users/42",
        Headers = new List<KvPair> { new("Accept", "application/json") },
        Params = new List<KvPair> { new("expand", "profile") },
    };

    private static RequestItem JsonPost() => new()
    {
        Method = "POST",
        Url = "https://api.acme.io/v1/users",
        Body = new BodyConfig { Mode = BodyMode.Json, Content = "{\"email\":\"a@b\"}" },
    };

    private static RequestItem WithBearer() => new()
    {
        Method = "GET",
        Url = "https://api.acme.io/v1/me",
        Auth = new AuthConfig
        {
            Type = AuthType.Bearer,
            Parameters = new Dictionary<string, string> { ["token"] = "tok-abc" }
        }
    };

    private static RequestItem GraphQLMultiOp() => new()
    {
        Method = "POST",
        Url = "https://api.acme.io/graphql",
        Body = new BodyConfig
        {
            Mode = BodyMode.GraphQL,
            GraphQLQuery = "query First { a } mutation Second { b }",
            GraphQLVariables = "{\"x\":1}",
        },
    };

    // ============================== Curl ==============================

    [Fact]
    public void Curl_GraphQLMultiOp_IncludesFirstOperationName()
    {
        var snippet = new CurlEmitter().Emit(GraphQLMultiOp());
        snippet.Should().Contain("\"operationName\":\"First\"");
        snippet.Should().Contain("\"variables\":{\"x\":1}");
    }

    [Fact]
    public void Curl_GraphQLSingleOp_OmitsOperationName()
    {
        var item = GraphQLMultiOp() with
        {
            Body = new BodyConfig { Mode = BodyMode.GraphQL, GraphQLQuery = "query Only { a }" },
        };
        var snippet = new CurlEmitter().Emit(item);
        snippet.Should().NotContain("operationName");
    }

    [Fact]
    public void Curl_GetWithHeader_ProducesExpectedShape()
    {
        var snippet = new CurlEmitter().Emit(SimpleGet());
        snippet.Should().StartWith("curl --request GET");
        snippet.Should().Contain("--url 'https://api.acme.io/v1/users/42?expand=profile'");
        snippet.Should().Contain("--header 'Accept: application/json'");
    }

    [Fact]
    public void Curl_JsonPost_IncludesContentTypeAndDataFlag()
    {
        var snippet = new CurlEmitter().Emit(JsonPost());
        snippet.Should().Contain("--request POST");
        snippet.Should().Contain("--header 'Content-Type: application/json'");
        snippet.Should().Contain("--data '{\"email\":\"a@b\"}'");
    }

    [Fact]
    public void Curl_BearerAuth_AppearsAsAuthorizationHeader()
    {
        var snippet = new CurlEmitter().Emit(WithBearer());
        snippet.Should().Contain("Authorization: Bearer tok-abc");
    }

    [Fact]
    public void Curl_InterpolatesPlaceholders()
    {
        var r = new RequestItem
        {
            Method = "GET",
            Url = "{{baseUrl}}/users/{{userId}}",
            Headers = new List<KvPair> { new("X-Tenant", "{{tenant}}") }
        };
        var vars = new Dictionary<string, string>
        {
            ["baseUrl"] = "https://api.x",
            ["userId"] = "42",
            ["tenant"] = "acme",
        };
        var snippet = new CurlEmitter().Emit(r, vars);
        snippet.Should().Contain("https://api.x/users/42");
        snippet.Should().Contain("X-Tenant: acme");
    }

    [Fact]
    public void Curl_SingleQuoteInValue_EscapedProperly()
    {
        var r = new RequestItem
        {
            Method = "GET", Url = "https://x",
            Headers = new List<KvPair> { new("X-Note", "it's fine") }
        };
        var snippet = new CurlEmitter().Emit(r);
        // The whole "X-Note: it's fine" is single-quoted, and the embedded ' becomes '\''
        // — the standard shell-safe trick for a single quote inside a single-quoted string.
        snippet.Should().Contain("'X-Note: it'\\''s fine'");
    }

    // ============================== JavaScript fetch ==============================

    [Fact]
    public void Fetch_HasFetchCall_WithMethodAndHeaders()
    {
        var snippet = new JavaScriptFetchEmitter().Emit(SimpleGet());
        snippet.Should().Contain("await fetch(");
        snippet.Should().Contain("method: \"GET\"");
        snippet.Should().Contain("\"Accept\": \"application/json\"");
        snippet.Should().Contain("?expand=profile");
    }

    [Fact]
    public void Fetch_PostsBody_WithContentTypeHeader()
    {
        var snippet = new JavaScriptFetchEmitter().Emit(JsonPost());
        snippet.Should().Contain("method: \"POST\"");
        snippet.Should().Contain("\"Content-Type\": \"application/json\"");
        snippet.Should().Contain("body: \"{\\\"email\\\":\\\"a@b\\\"}\"");
    }

    [Fact]
    public void Fetch_BearerAuth_ProducesAuthorizationHeader()
    {
        var snippet = new JavaScriptFetchEmitter().Emit(WithBearer());
        snippet.Should().Contain("\"Authorization\": \"Bearer tok-abc\"");
    }

    // ============================== Python requests ==============================

    [Fact]
    public void Python_GetUsesRequestsGet_WithUrlAndHeadersDicts()
    {
        var snippet = new PythonRequestsEmitter().Emit(SimpleGet());
        snippet.Should().Contain("import requests");
        snippet.Should().Contain("requests.get(");
        snippet.Should().Contain("url=\"https://api.acme.io/v1/users/42?expand=profile\"");
        snippet.Should().Contain("\"Accept\": \"application/json\"");
    }

    [Fact]
    public void Python_PostJson_UsesDataKeyword()
    {
        var snippet = new PythonRequestsEmitter().Emit(JsonPost());
        snippet.Should().Contain("requests.post(");
        snippet.Should().Contain("data=\"{\\\"email\\\":\\\"a@b\\\"}\"");
    }

    // ============================== C# HttpClient ==============================

    [Fact]
    public void CSharp_GetUsesHttpRequestMessage()
    {
        var snippet = new CSharpHttpClientEmitter().Emit(SimpleGet());
        snippet.Should().Contain("HttpMethod.Get");
        snippet.Should().Contain("https://api.acme.io/v1/users/42?expand=profile");
        snippet.Should().Contain("TryAddWithoutValidation(@\"Accept\", @\"application/json\")");
        snippet.Should().Contain("await client.SendAsync(request)");
    }

    [Fact]
    public void CSharp_PostJson_UsesStringContent()
    {
        var snippet = new CSharpHttpClientEmitter().Emit(JsonPost());
        snippet.Should().Contain("HttpMethod.Post");
        snippet.Should().Contain("new StringContent(");
        snippet.Should().Contain("application/json");
    }

    // ============================== Registry ==============================

    [Fact]
    public void Registry_LooksUpByLanguage_ReturnsFirstOfFamily()
    {
        CodegenRegistry.Find("curl").Should().BeOfType<CurlEmitter>();
        CodegenRegistry.Find("javascript").Should().BeOfType<JavaScriptFetchEmitter>();
        CodegenRegistry.Find("python").Should().BeOfType<PythonHttpClientEmitter>();
        CodegenRegistry.Find("csharp").Should().BeOfType<CSharpHttpClientEmitter>();
        CodegenRegistry.Find("rust").Should().BeNull();
    }

    [Fact]
    public void Registry_LooksUpByDisplayName()
    {
        CodegenRegistry.FindByDisplayName("NodeJs - Axios").Should().BeOfType<NodeJsAxiosEmitter>();
        CodegenRegistry.FindByDisplayName("Python - Requests").Should().BeOfType<PythonRequestsEmitter>();
        CodegenRegistry.FindByDisplayName("nope").Should().BeNull();
    }

    [Fact]
    public void Registry_MatchesPostmanLanguageList_InAlphabeticalOrder()
    {
        var names = CodegenRegistry.All.Select(e => e.DisplayName).ToList();
        names.Should().BeEquivalentTo(new[]
        {
            "C - libcurl", "C# - HttpClient", "C# - RestSharp", "cURL",
            "Dart - dio", "Dart - http", "Go - Native", "HTTP",
            "Java - OkHttp", "Java - Unirest",
            "JavaScript - Fetch", "JavaScript - jQuery", "JavaScript - XHR",
            "Kotlin - OkHttp",
            "NodeJs - Axios", "NodeJs - Native", "NodeJs - Request", "NodeJs - Unirest",
            "Objective-C - NSURLSession", "OCaml - Cohttp",
            "PHP - cURL", "PHP - Guzzle", "PHP - HTTP_Request2",
            "PowerShell - RestMethod",
            "Python - http.client", "Python - Requests",
            "R - httr", "R - RCurl", "Ruby - Net::HTTP",
            "Shell - HTTPie", "Shell - wget", "Swift - URLSession",
        }, opt => opt.WithStrictOrdering());
    }

    [Fact]
    public void Registry_DisplayNames_AreUnique()
    {
        CodegenRegistry.All.Select(e => e.DisplayName).Should().OnlyHaveUniqueItems();
    }

    // ============================== All-emitter smoke tests ==============================

    public static TheoryData<string> AllEmitterNames()
    {
        var data = new TheoryData<string>();
        foreach (var e in CodegenRegistry.All) data.Add(e.DisplayName);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllEmitterNames))]
    public void Emitter_Get_MentionsHostAndHeader(string displayName)
    {
        var emitter = CodegenRegistry.FindByDisplayName(displayName)!;
        var snippet = emitter.Emit(SimpleGet());
        snippet.Should().NotBeNullOrWhiteSpace();
        // Emitters that address host/path separately (HTTP raw, http.client, Node native)
        // still surface the host; everything else embeds the full URL.
        snippet.Should().Contain("api.acme.io");
        snippet.Should().Contain("expand=profile");
        snippet.Should().Contain("Accept");
    }

    [Theory]
    [MemberData(nameof(AllEmitterNames))]
    public void Emitter_JsonPost_CarriesBodyAndContentType(string displayName)
    {
        var emitter = CodegenRegistry.FindByDisplayName(displayName)!;
        var snippet = emitter.Emit(JsonPost());
        snippet.Should().ContainEquivalentOf("post");            // verb, whatever the casing/idiom
        snippet.Should().Contain("a@b");                          // body payload survives escaping
        snippet.Should().Contain("application/json");             // inferred Content-Type surfaces
    }

    [Theory]
    [MemberData(nameof(AllEmitterNames))]
    public void Emitter_BearerAuth_SurfacesAuthorizationHeader(string displayName)
    {
        var emitter = CodegenRegistry.FindByDisplayName(displayName)!;
        var snippet = emitter.Emit(WithBearer());
        snippet.Should().Contain("Authorization");
        snippet.Should().Contain("Bearer tok-abc");
    }
}
