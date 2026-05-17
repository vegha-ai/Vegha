using System.Text.Json.Nodes;
using Vegha.App.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Tests for the WebSocket workspace polish: subprotocols + headers parsing,
/// JSON outgoing format, hex outgoing format, and the JSON-on-receive auto-formatter.
/// We can't connect to a real socket from a unit test, so we exercise the helper
/// behaviour by configuring the VM and reading back state.</summary>
public class WebSocketViewModelTests
{
    private static WebSocketViewModel NewVm()
        => new(NullLogger<WebSocketViewModel>.Instance);

    [Fact]
    public void OutgoingFormat_DefaultsToText_AndOptionsIncludeJsonAndBinary()
    {
        var vm = NewVm();
        vm.OutgoingFormat.Should().Be("text");
        vm.OutgoingFormatOptions.Should().BeEquivalentTo(new[] { "text", "json", "binary" });
    }

    [Fact]
    public void AutoFormatReceivedJson_DefaultsTrue_ReconnectOnError_DefaultsFalse()
    {
        var vm = NewVm();
        vm.AutoFormatReceivedJson.Should().BeTrue();
        vm.ReconnectOnError.Should().BeFalse();
    }

    [Theory]
    [InlineData("graphql-ws, json", new[] { "graphql-ws", "json" })]
    [InlineData("graphql-ws json", new[] { "graphql-ws", "json" })]
    [InlineData("  ,, a  ,, b ", new[] { "a", "b" })]
    [InlineData("", new string[0])]
    public void Subprotocols_TextSplitsOnCommasAndWhitespace(string raw, string[] expected)
    {
        var got = InvokeStaticEnumerable("ParseSubprotocols", raw).ToArray();
        got.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData("Authorization: Bearer xyz", new[] { "Authorization", "Bearer xyz" })]
    [InlineData("X-Trace: abc\nX-Foo: bar", new[] { "X-Trace", "abc", "X-Foo", "bar" })]
    [InlineData("# comment line\nKey: V", new[] { "Key", "V" })]
    [InlineData("malformed-no-colon", new string[0])]
    public void RequestHeaders_ParseFromMultiLineKvText(string raw, string[] expected)
    {
        var pairs = InvokeStaticEnumerable<KeyValuePair<string, string>>("ParseHeaders", raw).ToArray();
        var flat = pairs.SelectMany(kv => new[] { kv.Key, kv.Value }).ToArray();
        flat.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void TryFormatJson_PrettyPrintsValidJson_ReturnsNullForInvalid()
    {
        var pretty = InvokeStaticString("TryFormatJson", "{\"a\":1,\"b\":2}");
        pretty.Should().NotBeNull();
        pretty!.Should().Contain("\n");
        pretty.Should().Contain("\"a\": 1");

        InvokeStaticString("TryFormatJson", "not json")
            .Should().BeNull();
    }

    [Fact]
    public void ParseHex_HandlesWhitespaceAnd0xPrefix()
    {
        var bytes = InvokeStaticBytes("ParseHex", "0xAA BB CC");
        bytes.Should().BeEquivalentTo(new byte[] { 0xAA, 0xBB, 0xCC });
    }

    // ---- Reflection helpers — the parsers are private statics. ----
    private static System.Reflection.MethodInfo Method(string name) =>
        typeof(WebSocketViewModel).GetMethod(name,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
        ?? throw new MissingMethodException("WebSocketViewModel." + name);

    private static IEnumerable<string> InvokeStaticEnumerable(string method, string arg)
        => InvokeStaticEnumerable<string>(method, arg);

    private static IEnumerable<T> InvokeStaticEnumerable<T>(string method, string arg)
        => (IEnumerable<T>)Method(method).Invoke(null, new object?[] { arg })!;

    private static string? InvokeStaticString(string method, string arg)
        => (string?)Method(method).Invoke(null, new object?[] { arg });

    private static byte[] InvokeStaticBytes(string method, string arg)
        => (byte[])Method(method).Invoke(null, new object?[] { arg })!;
}
