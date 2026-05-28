using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class CurlImporterTests
{
    [Theory]
    [InlineData("curl https://example.com", true)]
    [InlineData("  curl https://example.com", true)]
    [InlineData("CURL https://example.com", true)]
    [InlineData("curl\thttps://example.com", true)]
    [InlineData("curlhttps://example.com", false)]
    [InlineData("https://example.com", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("curl", false)]
    public void LooksLikeCurl_RecognizesCurlPrefix(string input, bool expected)
    {
        CurlImporter.LooksLikeCurl(input).Should().Be(expected);
    }

    [Fact]
    public void Bash_SimpleGet_ExtractsUrlAndMethod()
    {
        const string curl = "curl 'https://api.example.com/users'";

        var item = CurlImporter.Parse(curl);

        item.Method.Should().Be("GET");
        item.Url.Should().Be("https://api.example.com/users");
        item.Headers.Should().BeEmpty();
        item.Body.Mode.Should().Be(BodyMode.None);
        item.Auth.Should().BeNull();
    }

    [Fact]
    public void Bash_MultilineWithHeaders_ParsesContinuationsAndHeaders()
    {
        const string curl = """
            curl 'https://api.example.com/users' \
              -H 'accept: application/json' \
              -H 'x-trace: abc-123'
            """;

        var item = CurlImporter.Parse(curl);

        item.Method.Should().Be("GET");
        item.Url.Should().Be("https://api.example.com/users");
        item.Headers.Should().HaveCount(2);
        item.Headers[0].Name.Should().Be("accept");
        item.Headers[0].Value.Should().Be("application/json");
        item.Headers[1].Name.Should().Be("x-trace");
        item.Headers[1].Value.Should().Be("abc-123");
    }

    [Fact]
    public void Bash_PostJsonBody_DetectsJsonAndDefaultsMethodToPost()
    {
        const string curl = """
            curl 'https://api.example.com/users' \
              -H 'content-type: application/json' \
              -d '{"name":"vamsi"}'
            """;

        var item = CurlImporter.Parse(curl);

        item.Method.Should().Be("POST");
        item.Body.Mode.Should().Be(BodyMode.Json);
        item.Body.Content.Should().Be("{\"name\":\"vamsi\"}");
    }

    [Fact]
    public void Bash_BasicAuthHeader_PromotedToAuthConfig()
    {
        // "Aladdin:open sesame" base64-encoded
        const string curl = "curl https://example.com -H 'Authorization: Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ=='";

        var item = CurlImporter.Parse(curl);

        item.Headers.Should().NotContain(h =>
            string.Equals(h.Name, "Authorization", System.StringComparison.OrdinalIgnoreCase));
        item.Auth.Should().NotBeNull();
        item.Auth!.Type.Should().Be(AuthType.Basic);
        item.Auth.Parameters["username"].Should().Be("Aladdin");
        item.Auth.Parameters["password"].Should().Be("open sesame");
    }

    [Fact]
    public void Bash_BearerAuthHeader_PromotedToBearerConfig()
    {
        const string curl = "curl https://example.com -H 'Authorization: Bearer eyJabc.def.ghi'";

        var item = CurlImporter.Parse(curl);

        item.Headers.Should().BeEmpty();
        item.Auth.Should().NotBeNull();
        item.Auth!.Type.Should().Be(AuthType.Bearer);
        item.Auth.Parameters["token"].Should().Be("eyJabc.def.ghi");
    }

    [Fact]
    public void Bash_UserOption_BecomesBasicAuth()
    {
        const string curl = "curl https://example.com -u 'alice:s3cret'";

        var item = CurlImporter.Parse(curl);

        item.Auth.Should().NotBeNull();
        item.Auth!.Type.Should().Be(AuthType.Basic);
        item.Auth.Parameters["username"].Should().Be("alice");
        item.Auth.Parameters["password"].Should().Be("s3cret");
    }

    [Fact]
    public void Bash_CookieOption_MergedIntoCookieHeader()
    {
        const string curl = "curl https://example.com -b 'a=1' -b 'b=2'";

        var item = CurlImporter.Parse(curl);

        var cookie = item.Headers.Should().ContainSingle().Subject;
        cookie.Name.Should().Be("Cookie");
        cookie.Value.Should().Be("a=1; b=2");
    }

    [Fact]
    public void Bash_ExplicitMethod_OverridesDefault()
    {
        const string curl = "curl -X DELETE https://example.com/users/42";

        var item = CurlImporter.Parse(curl);

        item.Method.Should().Be("DELETE");
        item.Body.Mode.Should().Be(BodyMode.None);
    }

    [Fact]
    public void Bash_QueryString_ExtractedIntoParams()
    {
        const string curl = "curl 'https://example.com/users?page=2&size=20'";

        var item = CurlImporter.Parse(curl);

        item.Url.Should().Be("https://example.com/users");
        item.Params.Should().HaveCount(2);
        item.Params[0].Name.Should().Be("page");
        item.Params[0].Value.Should().Be("2");
        item.Params[1].Name.Should().Be("size");
        item.Params[1].Value.Should().Be("20");
    }

    [Fact]
    public void Bash_FormUrlEncodedBody_BreaksIntoFormDataRows()
    {
        const string curl = "curl https://example.com/login " +
                            "-H 'content-type: application/x-www-form-urlencoded' " +
                            "-d 'user=alice&pass=s3cret'";

        var item = CurlImporter.Parse(curl);

        item.Body.Mode.Should().Be(BodyMode.FormUrlEncoded);
        item.Body.FormData.Should().HaveCount(2);
        item.Body.FormData[0].Name.Should().Be("user");
        item.Body.FormData[0].Value.Should().Be("alice");
        item.Body.FormData[1].Name.Should().Be("pass");
        item.Body.FormData[1].Value.Should().Be("s3cret");
    }

    [Fact]
    public void Bash_MultipartForm_PopulatesMultipartItems()
    {
        const string curl = "curl https://example.com/upload " +
                            "-F 'name=alice' " +
                            "-F 'avatar=@/tmp/a.png'";

        var item = CurlImporter.Parse(curl);

        item.Method.Should().Be("POST");
        item.Body.Mode.Should().Be(BodyMode.MultipartForm);
        item.Body.MultipartItems.Should().HaveCount(2);
        item.Body.MultipartItems[0].Kind.Should().Be("text");
        item.Body.MultipartItems[0].Value.Should().Be("alice");
        item.Body.MultipartItems[1].Kind.Should().Be("file");
        item.Body.MultipartItems[1].Value.Should().Be("/tmp/a.png");
    }

    [Fact]
    public void Cmd_ChromeCopyAsCurl_ParsesUrlHeadersAndCookie()
    {
        // Verbatim Chrome "Copy as cURL (cmd)" output from the issue, multi-line with ^ continuation.
        const string curl = """
            curl ^"https://uat.bge.reach-pc.com/reach/creator/web/getNotificationTree^" ^
              -H ^"accept: application/json, text/javascript, */*; q=0.01^" ^
              -H ^"accept-language: en-US,en;q=0.9^" ^
              -b ^"JSESSIONID=DC4C8682BBD2A99FD0F03BB7AD335766^" ^
              -H ^"sec-ch-ua: ^\^"Chromium^\^";v=^\^"148^\^"^" ^
              -H ^"x-requested-with: XMLHttpRequest^"
            """;

        var item = CurlImporter.Parse(curl);

        item.Method.Should().Be("GET");
        item.Url.Should().Be("https://uat.bge.reach-pc.com/reach/creator/web/getNotificationTree");

        item.Headers.Should().Contain(h => h.Name == "accept"
            && h.Value == "application/json, text/javascript, */*; q=0.01");
        item.Headers.Should().Contain(h => h.Name == "accept-language"
            && h.Value == "en-US,en;q=0.9");
        item.Headers.Should().Contain(h => h.Name == "sec-ch-ua"
            && h.Value == "\"Chromium\";v=\"148\"");
        item.Headers.Should().Contain(h => h.Name == "x-requested-with"
            && h.Value == "XMLHttpRequest");

        item.Headers.Should().Contain(h => h.Name == "Cookie"
            && h.Value == "JSESSIONID=DC4C8682BBD2A99FD0F03BB7AD335766");
    }

    [Fact]
    public void Cmd_LineContinuationsCollapsedByEditor_StillParses()
    {
        // The URL field strips \n on paste — verify we still split on the stripped-continuation
        // case where "^\n  " collapsed to "^   ".
        const string curl =
            "curl ^\"https://example.com/api^\" ^   -H ^\"accept: application/json^\" ^   -H ^\"x-trace: abc^\"";

        var item = CurlImporter.Parse(curl);

        item.Url.Should().Be("https://example.com/api");
        item.Headers.Should().HaveCount(2);
        item.Headers[0].Name.Should().Be("accept");
        item.Headers[0].Value.Should().Be("application/json");
        item.Headers[1].Name.Should().Be("x-trace");
        item.Headers[1].Value.Should().Be("abc");
    }

    [Fact]
    public void PowerShell_BacktickContinuation_ParsesIntoTokens()
    {
        // PowerShell quoting uses double quotes and backtick as escape/continuation.
        const string curl = "curl \"https://example.com/api\" `\n  -H \"accept: application/json\" `\n  -H \"x-trace: abc\"";

        var item = CurlImporter.Parse(curl);

        item.Url.Should().Be("https://example.com/api");
        item.Headers.Should().HaveCount(2);
        item.Headers[0].Name.Should().Be("accept");
        item.Headers[0].Value.Should().Be("application/json");
    }

    [Fact]
    public void DataRaw_TreatedAsLiteralBody()
    {
        const string curl = "curl https://example.com -H 'content-type: application/json' --data-raw '{\"a\":1}'";

        var item = CurlImporter.Parse(curl);

        item.Method.Should().Be("POST");
        item.Body.Mode.Should().Be(BodyMode.Json);
        item.Body.Content.Should().Be("{\"a\":1}");
    }

    [Fact]
    public void HeadFlag_SetsMethodHead()
    {
        const string curl = "curl -I https://example.com";

        var item = CurlImporter.Parse(curl);

        item.Method.Should().Be("HEAD");
    }

    [Fact]
    public void UserAgentFlag_BecomesHeader()
    {
        const string curl = "curl https://example.com -A 'MyAgent/1.0'";

        var item = CurlImporter.Parse(curl);

        item.Headers.Should().ContainSingle()
            .Which.Name.Should().Be("User-Agent");
        item.Headers[0].Value.Should().Be("MyAgent/1.0");
    }

    [Fact]
    public void TryParse_NonCurlInput_ReturnsItemWithNoUrl()
    {
        // TryParse never throws — even garbage input falls through to a defaulted RequestItem.
        CurlImporter.TryParse("this is not curl", out var item).Should().BeTrue();
        item.Should().NotBeNull();
    }
}
