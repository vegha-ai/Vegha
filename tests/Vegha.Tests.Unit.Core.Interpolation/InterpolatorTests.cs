using Vegha.Core.Interpolation;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Interpolation;

public class InterpolatorTests
{
    [Fact]
    public void Resolve_SimpleSubstitution()
    {
        var result = Interpolator.Resolve("{{baseUrl}}/users",
            new Dictionary<string, string> { ["baseUrl"] = "https://api.acme.io" });
        result.Should().Be("https://api.acme.io/users");
    }

    [Fact]
    public void Resolve_MultiplePlaceholders()
    {
        var result = Interpolator.Resolve("{{base}}/users/{{id}}",
            new Dictionary<string, string>
            {
                ["base"] = "https://api.x",
                ["id"] = "42"
            });
        result.Should().Be("https://api.x/users/42");
    }

    [Fact]
    public void Resolve_LeavesUnknownPlaceholderLiteral()
    {
        var result = Interpolator.Resolve("{{baseUrl}}/x/{{missing}}",
            new Dictionary<string, string> { ["baseUrl"] = "X" });
        result.Should().Be("X/x/{{missing}}");
    }

    [Fact]
    public void Resolve_NestedReferences()
    {
        var result = Interpolator.Resolve("{{full}}",
            new Dictionary<string, string>
            {
                ["full"] = "{{scheme}}://{{host}}",
                ["scheme"] = "https",
                ["host"] = "api.acme.io"
            });
        result.Should().Be("https://api.acme.io");
    }

    [Fact]
    public void Resolve_DetectsDirectCycle_LeavesLiteral()
    {
        var result = Interpolator.Resolve("{{a}}",
            new Dictionary<string, string> { ["a"] = "{{a}}" });
        result.Should().Be("{{a}}");
    }

    [Fact]
    public void Resolve_DetectsIndirectCycle_LeavesLiteral()
    {
        var result = Interpolator.Resolve("{{a}}",
            new Dictionary<string, string>
            {
                ["a"] = "{{b}}",
                ["b"] = "{{c}}",
                ["c"] = "{{a}}"
            });
        // The chain a -> b -> c, then c references back to a (already in chain) → leave {{a}} literal
        result.Should().Be("{{a}}");
    }

    [Fact]
    public void Resolve_UnclosedBracesPassedThrough()
    {
        var result = Interpolator.Resolve("hello {{unclosed",
            new Dictionary<string, string>());
        result.Should().Be("hello {{unclosed");
    }

    [Fact]
    public void Resolve_EmptyPlaceholderLeftAlone()
    {
        var result = Interpolator.Resolve("a {{}} b", new Dictionary<string, string>());
        result.Should().Be("a {{}} b");
    }

    [Fact]
    public void Resolve_NoPlaceholders_ReturnsInputUnchanged()
    {
        var result = Interpolator.Resolve("plain text", new Dictionary<string, string>());
        result.Should().Be("plain text");
    }

    [Fact]
    public void Resolve_TrimsWhitespaceInPlaceholderName()
    {
        var result = Interpolator.Resolve("{{  baseUrl  }}",
            new Dictionary<string, string> { ["baseUrl"] = "X" });
        result.Should().Be("X");
    }

    [Fact]
    public void Resolve_CustomResolver()
    {
        var result = Interpolator.Resolve("hello {{name}}",
            n => n == "name" ? "world" : null);
        result.Should().Be("hello world");
    }

    [Fact]
    public void Resolve_IsCaseSensitive()
    {
        var result = Interpolator.Resolve("{{Name}}",
            new Dictionary<string, string> { ["name"] = "lowercase" });
        result.Should().Be("{{Name}}"); // not matched
    }

    [Fact]
    public void Resolve_HandlesEmptyTemplate()
    {
        Interpolator.Resolve("", new Dictionary<string, string>()).Should().Be("");
    }

    [Fact]
    public void Resolve_HandlesAdjacentPlaceholders()
    {
        var result = Interpolator.Resolve("{{a}}{{b}}",
            new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        result.Should().Be("12");
    }

    [Fact]
    public void Resolve_MaxDepthGuard()
    {
        // Build a chain a→b→c→...→z that references one beyond.
        var vars = new Dictionary<string, string>();
        for (int i = 0; i < 100; i++)
        {
            var key = "v" + i;
            var next = "v" + (i + 1);
            vars[key] = "{{" + next + "}}";
        }
        // v100 missing → would normally leave {{v100}} literal at the end.
        // The chain length exceeds MaxDepth (32), so we should bail out cleanly.
        var result = Interpolator.Resolve("{{v0}}", vars);
        // Doesn't matter exactly what we get; the test asserts no stack overflow / infinite loop.
        result.Should().NotBeNullOrEmpty();
    }
}
