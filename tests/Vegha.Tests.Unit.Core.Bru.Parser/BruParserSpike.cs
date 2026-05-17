using Vegha.Core.Bru.Parser;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Bru.Parser;

/// <summary>Smoke tests covering the bedrock cases first proven in the Pidgin spike.</summary>
public class BruParserSpike
{
    [Fact]
    public void ParsesPingFixture()
    {
        const string bru = """
            meta {
              name: ping
              type: http
              seq: 1
            }

            get {
              url: {{host}}/ping
              body: none
              auth: none
            }

            script:pre-request {
              bru.runner.stopExecution();
            }
            """;

        var doc = BruParser.Parse(bru);

        doc.Blocks.Should().HaveCount(3);

        var meta = doc.Blocks[0].Should().BeOfType<DictBlock>().Subject;
        meta.Name.Should().Be("meta");
        meta.Pair("name").Should().Be("ping");
        meta.Pair("type").Should().Be("http");
        meta.Pair("seq").Should().Be("1");

        var get = doc.Blocks[1].Should().BeOfType<DictBlock>().Subject;
        get.Name.Should().Be("get");
        get.Pair("url").Should().Be("{{host}}/ping");
        get.Pair("body").Should().Be("none");
        get.Pair("auth").Should().Be("none");

        var script = doc.Blocks[2].Should().BeOfType<TextBlock>().Subject;
        script.Name.Should().Be("script:pre-request");
        script.Text.Should().Contain("bru.runner.stopExecution();");
    }

    [Fact]
    public void ParsesEmptyDocument()
    {
        var doc = BruParser.Parse("");
        doc.Blocks.Should().BeEmpty();
    }

    [Fact]
    public void ParsesSingleMetaBlock()
    {
        const string bru = """
            meta {
              name: hello
            }
            """;

        var doc = BruParser.Parse(bru);
        doc.Blocks.Should().HaveCount(1);
        var meta = doc.Blocks[0].Should().BeOfType<DictBlock>().Subject;
        meta.Pair("name").Should().Be("hello");
    }

    [Fact]
    public void ParsesTextBlockAlone()
    {
        const string bru = "script:pre-request {\n  hello;\n}\n";
        var doc = BruParser.Parse(bru);
        doc.Blocks.Should().HaveCount(1);
        var s = doc.Blocks[0].Should().BeOfType<TextBlock>().Subject;
        s.Name.Should().Be("script:pre-request");
        s.Text.Should().Be("  hello;");
    }

    [Fact]
    public void ParsesDictThenTextBlock()
    {
        const string bru = "meta {\n  name: x\n}\n\nscript:pre-request {\n  hello;\n}\n";
        var doc = BruParser.Parse(bru);
        doc.Blocks.Should().HaveCount(2);
        doc.Blocks[0].Should().BeOfType<DictBlock>();
        doc.Blocks[1].Should().BeOfType<TextBlock>();
    }

    [Fact]
    public void TryParseReturnsErrorOnMalformed()
    {
        var ok = BruParser.TryParse("meta { unclosed", out _, out var err);
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }
}

/// <summary>Test helpers — keeps assertions readable.</summary>
internal static class TestExtensions
{
    public static string Pair(this DictBlock block, string name)
    {
        var pair = block.Pairs.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"Pair '{name}' not found in {block.Name}");
        return pair.Value switch
        {
            StringValue s => s.Text,
            _ => throw new InvalidOperationException($"Pair '{name}' is not a StringValue")
        };
    }

    public static BruPair PairObj(this DictBlock block, string name) =>
        block.Pairs.First(p => p.Name == name);
}
