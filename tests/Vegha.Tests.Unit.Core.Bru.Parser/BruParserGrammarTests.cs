using Vegha.Core.Bru.Parser;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Bru.Parser;

/// <summary>Coverage for the full v2 grammar features beyond the spike.</summary>
public class BruParserGrammarTests
{
    // ============================== Quoted keys ==============================

    [Fact]
    public void QuotedKey_WithSpaces()
    {
        const string bru = """
            headers {
              "key with spaces": is allowed
            }
            """;
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        d.Pair("key with spaces").Should().Be("is allowed");
    }

    [Fact]
    public void QuotedKey_WithColon()
    {
        const string bru = """
            headers {
              "colon:header": is allowed
            }
            """;
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        d.Pair("colon:header").Should().Be("is allowed");
    }

    [Fact]
    public void QuotedKey_WithEscapedQuote()
    {
        const string bru = """
            headers {
              "nested escaped \"quote\"": is allowed
            }
            """;
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        d.Pair("nested escaped \"quote\"").Should().Be("is allowed");
    }

    [Fact]
    public void QuotedKey_WithBraces()
    {
        const string bru = """
            headers {
              "{braces}": is allowed
            }
            """;
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        d.Pair("{braces}").Should().Be("is allowed");
    }

    // ============================== Disable prefix ==============================

    [Fact]
    public void DisablePrefix_OnUnquotedKey()
    {
        const string bru = """
            headers {
              ~name: hello
            }
            """;
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        var p = d.Pairs[0];
        p.Name.Should().Be("name");
        p.Enabled.Should().BeFalse();
    }

    [Fact]
    public void DisablePrefix_OnQuotedKey()
    {
        const string bru = """
            headers {
              ~"disabled:colon:header": is allowed
            }
            """;
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        var p = d.Pairs[0];
        p.Name.Should().Be("disabled:colon:header");
        p.Enabled.Should().BeFalse();
    }

    // ============================== List values ==============================

    [Fact]
    public void ListValue_InMetaBlock()
    {
        const string bru = """
            meta {
              name: Foo
              tags: [
                foo
                bar
                baz
              ]
            }
            """;
        var meta = (DictBlock)BruParser.Parse(bru).Blocks[0];
        meta.Pair("name").Should().Be("Foo");
        var tags = meta.PairObj("tags").Value.Should().BeOfType<ListValue>().Subject;
        tags.Items.Should().Equal("foo", "bar", "baz");
    }

    [Fact]
    public void ListValue_Empty()
    {
        const string bru = """
            meta {
              tags: [
              ]
            }
            """;
        var meta = (DictBlock)BruParser.Parse(bru).Blocks[0];
        meta.PairObj("tags").Value.Should().BeOfType<ListValue>()
            .Which.Items.Should().BeEmpty();
    }

    // ============================== Multiline values ==============================

    [Fact]
    public void MultilineValue_WithContentType()
    {
        const string bru = "vars:pre-request {\n" +
                           "  big: '''hello\nworld''' @contentType(text/plain)\n" +
                           "}\n";
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        var v = d.PairObj("big").Value.Should().BeOfType<MultilineValue>().Subject;
        v.Text.Should().Be("hello\nworld");
        v.ContentType.Should().Be("text/plain");
    }

    [Fact]
    public void MultilineValue_WithoutContentType()
    {
        const string bru = "vars:pre-request {\n" +
                           "  big: '''line1\nline2'''\n" +
                           "}\n";
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        var v = d.PairObj("big").Value.Should().BeOfType<MultilineValue>().Subject;
        v.Text.Should().Be("line1\nline2");
        v.ContentType.Should().BeNull();
    }

    // ============================== Annotations ==============================

    [Fact]
    public void AnnotationOnPair_DoubleQuotedArgs()
    {
        const string bru = """
            vars:pre-request {
                @description("found in path")
                key: value
            }
            """;
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        d.Pairs.Should().HaveCount(1);
        var p = d.Pairs[0];
        p.Name.Should().Be("key");
        p.Annotations.Should().NotBeNull();
        p.Annotations!.Should().HaveCount(1);
        p.Annotations![0].Name.Should().Be("description");
        p.Annotations![0].RawArgs.Should().Be("\"found in path\"");
    }

    [Fact]
    public void MultipleAnnotationsOnDifferentPairs()
    {
        const string bru = """
            vars:pre-request {
                @description("found in C:\Users\File\Path")
                key:value
                @description("height of 2' ")
                key2:value
            }
            """;
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        d.Pairs.Should().HaveCount(2);
        d.Pairs[0].Annotations!.Single().Name.Should().Be("description");
        d.Pairs[1].Annotations!.Single().Name.Should().Be("description");
    }

    [Fact]
    public void KeyStartingWithAt_IsNotAnnotation()
    {
        // "@orderNumber: ..." — colon-after means it's a key, not an annotation.
        const string bru = """
            vars:post-response {
              @orderNumber: $res.body.orderNumber
            }
            """;
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        d.Pairs.Should().HaveCount(1);
        d.Pairs[0].Name.Should().Be("@orderNumber");
        d.Pairs[0].Annotations.Should().BeNull();
        d.Pair("@orderNumber").Should().Be("$res.body.orderNumber");
    }

    [Fact]
    public void DisablePrefix_PlusKeyStartingWithAt()
    {
        const string bru = """
            vars:post-response {
              ~@transactionId: $res.body.transactionId
            }
            """;
        var d = (DictBlock)BruParser.Parse(bru).Blocks[0];
        d.Pairs[0].Name.Should().Be("@transactionId");
        d.Pairs[0].Enabled.Should().BeFalse();
        d.Pairs[0].Annotations.Should().BeNull();
    }

    // ============================== Body / scripts ==============================

    [Fact]
    public void BodyJson_PreservesContentVerbatim()
    {
        const string bru = """
            body:json {
              {
                "hello": "world"
              }
            }
            """;
        var t = (TextBlock)BruParser.Parse(bru).Blocks[0];
        t.Name.Should().Be("body:json");
        t.Text.Should().Contain("\"hello\": \"world\"");
    }

    [Fact]
    public void BodyXml_PreservesContentVerbatim()
    {
        const string bru = """
            body:xml {
              <xml>
                <name>John</name>
              </xml>
            }
            """;
        var t = (TextBlock)BruParser.Parse(bru).Blocks[0];
        t.Name.Should().Be("body:xml");
        t.Text.Should().Contain("<name>John</name>");
    }

    [Fact]
    public void ExampleBlock_IsTextBlock()
    {
        const string bru = """
            example {
              name: Success 200
              status: 200
            }
            """;
        var t = (TextBlock)BruParser.Parse(bru).Blocks[0];
        t.Name.Should().Be("example");
        t.Text.Should().Contain("name: Success 200");
    }

    // ============================== Multiple top-level blocks ==============================

    [Fact]
    public void FullRequestFile_ParsesAllBlockTypes()
    {
        // Smaller version of bruno-tests/fixtures/request.bru — exercises every block kind.
        const string bru = """
            meta {
              name: Send Bulk SMS
              type: http
              seq: 1
              tags: [
                foo
                bar
              ]
            }

            get {
              url: https://api.textlocal.in/send/:id
              body: json
              auth: bearer
            }

            params:query {
              apiKey: secret
              "key with spaces": is allowed
              ~message: hello
            }

            headers {
              content-type: application/json
              ~transaction-id: {{transactionId}}
            }

            auth:bearer {
              token: 123
            }

            body:json {
              {
                "hello": "world"
              }
            }

            vars:pre-request {
              departingDate: 2020-01-01
              ~returningDate: 2020-01-02
            }

            assert {
              $res.status: 200
              ~$res.body.message: success
            }

            script:pre-request {
              const foo = 'bar';
            }

            tests {
              expect(response.status).to.equal(200);
            }

            docs {
              This request needs auth token.
            }
            """;

        var doc = BruParser.Parse(bru);
        doc.Blocks.Should().HaveCount(11);

        // Block names in order
        doc.Blocks.Select(b => b.Name).Should().Equal(
            "meta", "get", "params:query", "headers", "auth:bearer",
            "body:json", "vars:pre-request", "assert",
            "script:pre-request", "tests", "docs");

        // Tags list
        var meta = (DictBlock)doc.Blocks[0];
        meta.PairObj("tags").Value.Should().BeOfType<ListValue>()
            .Which.Items.Should().Equal("foo", "bar");

        // Disabled pair
        var queryParams = (DictBlock)doc.Blocks[2];
        queryParams.PairObj("message").Enabled.Should().BeFalse();

        // Quoted key
        queryParams.Pair("key with spaces").Should().Be("is allowed");

        // Body kept verbatim
        ((TextBlock)doc.Blocks[5]).Text.Should().Contain("\"hello\": \"world\"");
    }
}
