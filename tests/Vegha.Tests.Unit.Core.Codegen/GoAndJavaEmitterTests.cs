using Vegha.Core.Codegen;
using Vegha.Core.Domain;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Codegen;

public class GoAndJavaEmitterTests
{
    private static RequestItem Sample(string method = "POST", string? body = "{\"name\":\"a\"}") => new()
    {
        Name = "x",
        Method = method,
        Url = "https://api.test/users",
        Headers = new List<KvPair> { new("Authorization", "Bearer abc") },
        Body = body is null ? new BodyConfig() : new BodyConfig { Mode = BodyMode.Json, Content = body },
    };

    [Fact]
    public void Go_EmitsRequestWithBody_AndHeader()
    {
        var code = new GoNetHttpEmitter().Emit(Sample());
        code.Should().Contain("net/http");
        code.Should().Contain("\"POST\"");
        code.Should().Contain("\"https://api.test/users\"");
        code.Should().Contain("strings.NewReader");
        code.Should().Contain("\"Authorization\"");
        code.Should().Contain("\"Bearer abc\"");
    }

    [Fact]
    public void Go_GetWithoutBody_OmitsStringsImport()
    {
        var code = new GoNetHttpEmitter().Emit(Sample(method: "GET", body: null));
        code.Should().NotContain("strings.NewReader");
        code.Should().Contain("\"GET\"");
    }

    [Fact]
    public void Java_EmitsOkHttpClient_WithMediaTypeAndHeader()
    {
        var code = new JavaOkHttpEmitter().Emit(Sample());
        code.Should().Contain("import okhttp3.*;");
        code.Should().Contain("MediaType MEDIA = MediaType.parse");
        code.Should().Contain("\"POST\"");
        code.Should().Contain("\"https://api.test/users\"");
        code.Should().Contain("\"Authorization\"");
    }

    [Fact]
    public void RegistryListsBothNewEmitters()
    {
        CodegenRegistry.Find("go").Should().NotBeNull();
        CodegenRegistry.Find("java").Should().NotBeNull();
        CodegenRegistry.All.Select(e => e.Language).Should().Contain(new[] { "curl", "go", "java" });
    }
}
