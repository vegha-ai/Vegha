using Vegha.Core.Domain;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

/// <summary>Unit tests for <see cref="KvBulkEditParser"/> — the Bruno-parity bulk-edit
/// text parser used by the Params / Headers / Vars tabs.</summary>
public class KvBulkEditParserTests
{
    [Fact]
    public void Parse_EmptyOrNull_ReturnsEmpty()
    {
        KvBulkEditParser.Parse(null).Should().BeEmpty();
        KvBulkEditParser.Parse("").Should().BeEmpty();
        KvBulkEditParser.Parse("   \n  \n").Should().BeEmpty();
    }

    [Fact]
    public void Parse_ColonSyntax_ParsesHeaderShape()
    {
        var text = "Authorization: Bearer abc\nContent-Type: application/json";
        var rows = KvBulkEditParser.Parse(text);
        rows.Should().HaveCount(2);
        rows[0].Name.Should().Be("Authorization");
        rows[0].Value.Should().Be("Bearer abc");
        rows[1].Name.Should().Be("Content-Type");
        rows[1].Value.Should().Be("application/json");
    }

    [Fact]
    public void Parse_EqualsSyntax_ParsesParamShape()
    {
        var text = "id=42\nrole=admin";
        var rows = KvBulkEditParser.Parse(text);
        rows.Should().HaveCount(2);
        rows[0].Name.Should().Be("id");
        rows[0].Value.Should().Be("42");
        rows[1].Name.Should().Be("role");
        rows[1].Value.Should().Be("admin");
    }

    [Fact]
    public void Parse_MixedSyntax_RespectsFirstSeparatorPerLine()
    {
        var text = "a: 1\nb=2\nc: x=y";
        var rows = KvBulkEditParser.Parse(text);
        rows.Should().HaveCount(3);
        rows[0].Name.Should().Be("a");      rows[0].Value.Should().Be("1");
        rows[1].Name.Should().Be("b");      rows[1].Value.Should().Be("2");
        // Line "c: x=y" — colon comes first, so name=c, value="x=y".
        rows[2].Name.Should().Be("c");      rows[2].Value.Should().Be("x=y");
    }

    [Fact]
    public void Parse_TildePrefix_MarksRowDisabled()
    {
        var rows = KvBulkEditParser.Parse("~X-Trace: id\nAccept: */*");
        rows.Should().HaveCount(2);
        rows[0].Enabled.Should().BeFalse("the ~ prefix disables the row");
        rows[1].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Parse_HashPrefix_IsSkippedAsComment()
    {
        var rows = KvBulkEditParser.Parse("# this is a comment\nA: B");
        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("A");
    }

    [Fact]
    public void Parse_NameOnly_KeepsRowWithEmptyValue()
    {
        var rows = KvBulkEditParser.Parse("Just-A-Name");
        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("Just-A-Name");
        rows[0].Value.Should().BeEmpty();
    }

    [Fact]
    public void Parse_HandlesValueContainingColon()
    {
        var rows = KvBulkEditParser.Parse("X-Time: 2026-05-12T10:30:00");
        rows.Should().HaveCount(1);
        rows[0].Value.Should().Be("2026-05-12T10:30:00");
    }

    [Fact]
    public void Format_RoundTripsParseOutput()
    {
        // Format always emits `Key: Value`; round-trip through Parse → Format → Parse should
        // yield the same set of rows (modulo whitespace normalization).
        var initial = new[]
        {
            new KvPair("Authorization", "Bearer abc", true),
            new KvPair("X-Disabled",   "x",         false),
        };
        var text = KvBulkEditParser.Format(initial);
        var roundTripped = KvBulkEditParser.Parse(text);
        roundTripped.Should().HaveCount(2);
        roundTripped[0].Name.Should().Be("Authorization");
        roundTripped[0].Value.Should().Be("Bearer abc");
        roundTripped[0].Enabled.Should().BeTrue();
        roundTripped[1].Name.Should().Be("X-Disabled");
        roundTripped[1].Enabled.Should().BeFalse();
    }
}
