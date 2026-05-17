using Vegha.Core.Bru.Parser;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Bru.Parser;

/// <summary>
/// Validates the parser against the real fixtures shipped with Bruno
/// (<c>bruno/packages/bruno-lang/v2/tests/fixtures/</c>). This is the
/// strongest fidelity check we can apply short of running Bruno's own
/// JS test suite.
/// </summary>
public class BruFixtureTests
{
    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void RequestBru_ParsesWithoutError()
    {
        var bru = ReadFixture("request.bru");
        var ok = BruParser.TryParse(bru, out var doc, out var error);
        ok.Should().BeTrue($"parse failed: {error}");
        doc.Blocks.Should().HaveCountGreaterThan(20); // ~25 blocks in this fixture
    }

    [Fact]
    public void RequestBru_HasExpectedBlocks()
    {
        var bru = ReadFixture("request.bru");
        var doc = BruParser.Parse(bru);
        var names = doc.Blocks.Select(b => b.Name).ToList();

        // Spot-check the major sections
        names.Should().Contain("meta");
        names.Should().Contain("get");
        names.Should().Contain("params:query");
        names.Should().Contain("params:path");
        names.Should().Contain("headers");
        names.Should().Contain("auth:awsv4");
        names.Should().Contain("auth:basic");
        names.Should().Contain("auth:bearer");
        names.Should().Contain("auth:digest");
        names.Should().Contain("auth:oauth2");
        names.Should().Contain("auth:wsse");
        names.Should().Contain("body:json");
        names.Should().Contain("body:xml");
        names.Should().Contain("body:sparql");
        names.Should().Contain("body:form-urlencoded");
        names.Should().Contain("body:multipart-form");
        names.Should().Contain("body:file");
        names.Should().Contain("body:graphql");
        names.Should().Contain("body:graphql:vars");
        names.Should().Contain("vars:pre-request");
        names.Should().Contain("vars:post-response");
        names.Should().Contain("assert");
        names.Should().Contain("script:pre-request");
        names.Should().Contain("tests");
        names.Should().Contain("docs");
    }

    [Fact]
    public void RequestBru_TagsListInMeta()
    {
        var doc = BruParser.Parse(ReadFixture("request.bru"));
        var meta = (DictBlock)doc.Blocks[0];
        var tagsList = meta.Pairs.Single(p => p.Name == "tags").Value
            .Should().BeOfType<ListValue>().Subject;
        tagsList.Items.Should().Equal("foo", "bar");
    }

    [Fact]
    public void RequestBru_QuotedKeys_AndDisablePrefix_InQueryParams()
    {
        var doc = BruParser.Parse(ReadFixture("request.bru"));
        var qp = (DictBlock)doc.Blocks.First(b => b.Name == "params:query");

        qp.Pairs.Should().Contain(p => p.Name == "key with spaces" && p.Enabled);
        qp.Pairs.Should().Contain(p => p.Name == "colon:parameter" && p.Enabled);
        qp.Pairs.Should().Contain(p => p.Name == "nested escaped \"quote\"" && p.Enabled);
        qp.Pairs.Should().Contain(p => p.Name == "{braces}" && p.Enabled);

        // Disabled + quoted-with-colon
        qp.Pairs.Should().Contain(p => p.Name == "disabled:colon:parameter" && !p.Enabled);
        // Disabled + plain
        qp.Pairs.Should().Contain(p => p.Name == "message" && !p.Enabled);
    }

    [Fact]
    public void RequestBru_VarsPostResponse_AtKeysAreNotAnnotations()
    {
        var doc = BruParser.Parse(ReadFixture("request.bru"));
        var vars = (DictBlock)doc.Blocks.First(b => b.Name == "vars:post-response");

        // @orderNumber and ~@transactionId are KEYS (annotations require ~":" lookahead).
        vars.Pairs.Should().Contain(p => p.Name == "@orderNumber" && p.Enabled);
        vars.Pairs.Should().Contain(p => p.Name == "@transactionId" && !p.Enabled);
        vars.Pairs.All(p => p.Annotations is null || p.Annotations.Count == 0)
            .Should().BeTrue("no pair in vars:post-response carries an annotation");
    }

    [Fact]
    public void RequestBru_BodyJsonPreservesContent()
    {
        var doc = BruParser.Parse(ReadFixture("request.bru"));
        var body = doc.Blocks.OfType<TextBlock>().First(b => b.Name == "body:json");
        body.Text.Should().Contain("\"hello\": \"world\"");
    }

    [Fact]
    public void AnnotationsBru_BothPairsHaveDescriptionAnnotation()
    {
        var doc = BruParser.Parse(ReadFixture("annotations.bru"));
        doc.Blocks.Should().HaveCount(1);

        var d = (DictBlock)doc.Blocks[0];
        d.Name.Should().Be("vars:pre-request");
        d.Pairs.Should().HaveCount(2);

        d.Pairs[0].Annotations.Should().NotBeNull();
        d.Pairs[0].Annotations![0].Name.Should().Be("description");
        d.Pairs[0].Annotations![0].RawArgs.Should().Contain("found in C:\\Users\\File\\Path");

        d.Pairs[1].Annotations.Should().NotBeNull();
        d.Pairs[1].Annotations![0].Name.Should().Be("description");
        d.Pairs[1].Annotations![0].RawArgs.Should().Contain("height of 2'");
    }
}
