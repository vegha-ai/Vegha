using Vegha.App.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>VM-level coverage for the body-pane filters: JSONPath, XML pretty-print,
/// and the save-filename helper. We poke private behaviour by setting properties and
/// reading <see cref="RequestEditorViewModel.DisplayedBody"/> + the suggested filename.</summary>
public class ResponseDisplayHelpersTests
{
    private static RequestEditorViewModel NewVm() =>
        new(executor: new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            oauth2: new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            scriptHost: new Vegha.Core.Scripting.JintHost(),
            logger: NullLogger<RequestEditorViewModel>.Instance);

    [Fact]
    public void DisplayedBody_RawFormat_PreservesBodyByteForByte()
    {
        // Raw mode skips every transformation (incl. the new auto-prettify) so users
        // can see exactly what came over the wire.
        var vm = NewVm();
        vm.ResponseBody = """{"name":"alice"}""";
        vm.ResponseContentType = "application/json";
        vm.ResponseFormat = "Raw";
        vm.DisplayedBody.Should().Be("""{"name":"alice"}""");
    }

    [Fact]
    public void DisplayedBody_AutoFormat_JsonPrettifies()
    {
        // Auto mode reformats JSON so the response viewer is readable by default.
        var vm = NewVm();
        vm.ResponseBody = """{"name":"alice"}""";
        vm.ResponseContentType = "application/json";
        vm.DisplayedBody.Should().Contain("\n");
        vm.DisplayedBody.Should().Contain("\"name\"");
    }

    [Fact]
    public void DisplayedBody_JsonPathFilter_ProjectsArray()
    {
        var vm = NewVm();
        vm.ResponseBody = """{"users":[{"email":"a@x"},{"email":"b@x"}]}""";
        vm.ResponseContentType = "application/json";
        vm.JsonPathFilter = "$.users[*].email";
        vm.DisplayedBody.Should().Contain("a@x");
        vm.DisplayedBody.Should().Contain("b@x");
    }

    [Fact]
    public void DisplayedBody_InvalidJsonPath_ReturnsErrorComment()
    {
        var vm = NewVm();
        vm.ResponseBody = """{"a":1}""";
        vm.ResponseContentType = "application/json";
        vm.JsonPathFilter = "this is not jsonpath";
        vm.DisplayedBody.Should().StartWith("// JSONPath error");
    }

    [Fact]
    public void DisplayedBody_XmlPrettyPrint_FormatsIndented()
    {
        var vm = NewVm();
        vm.ResponseBody = "<root><a>1</a><b>2</b></root>";
        vm.ResponseContentType = "application/xml";
        vm.XmlPrettyPrint = true;
        vm.DisplayedBody.Should().Contain("\n");
    }

    [Fact]
    public void DisplayedBody_RawFormat_XmlByteForByte()
    {
        // Auto-formats XML by default; Raw mode is the explicit opt-out.
        var vm = NewVm();
        vm.ResponseBody = "<root><a>1</a></root>";
        vm.ResponseContentType = "application/xml";
        vm.ResponseFormat = "Raw";
        vm.DisplayedBody.Should().Be("<root><a>1</a></root>");
    }

    [Theory]
    [InlineData("application/json", "https://api.test/users", "users.json")]
    [InlineData("application/xml", "https://api.test/foo.xml", "foo.xml.xml")]
    [InlineData("image/png", "https://x.test/avatar", "avatar.png")]
    [InlineData("application/pdf", "https://x.test/files/report", "report.pdf")]
    [InlineData("text/html", "https://x.test/", "x.test.html")]
    [InlineData("application/octet-stream", "", "response.bin")]
    public void SuggestedSaveFileName_PicksExtensionFromContentType_AndStemFromUrl(
        string contentType, string url, string expected)
    {
        var vm = NewVm();
        vm.Url = url;
        vm.ResponseContentType = contentType;
        vm.SuggestedSaveFileName().Should().Be(expected);
    }

    [Fact]
    public void ResponseIsImage_WhenContentTypeStartsWithImage()
    {
        var vm = NewVm();
        vm.ResponseContentType = "image/png";
        vm.ResponseIsImage.Should().BeTrue();
        vm.ResponseIsTextual.Should().BeFalse();
    }

    [Fact]
    public void ResponseIsPdf_WhenContentTypeIsApplicationPdf()
    {
        var vm = NewVm();
        vm.ResponseContentType = "application/pdf";
        vm.ResponseIsPdf.Should().BeTrue();
        vm.ResponseIsTextual.Should().BeFalse();
    }
}
