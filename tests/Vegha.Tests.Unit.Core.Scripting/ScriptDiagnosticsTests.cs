using Vegha.Core.Scripting;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class ScriptDiagnosticsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("bru.setVar('a', 'b');")]
    [InlineData("var t = res.getBody(); test('ok', function(){ expect(res.getStatus()).to.equal(200); });")]
    [InlineData("const x = {a: 1, b: [1,2,3]}; console.log(x);")]
    public void Clean_scripts_produce_no_diagnostics(string code)
    {
        ScriptDiagnostics.Analyze(code).Should().BeEmpty();
    }

    [Fact]
    public void Syntax_error_reports_offset_line_and_message()
    {
        // Stray token after '=' — a genuine syntax error.
        const string code = "var x = ;";
        var diags = ScriptDiagnostics.Analyze(code);

        diags.Should().HaveCount(1);
        var d = diags[0];
        d.Line.Should().Be(1);
        d.Offset.Should().BeInRange(0, code.Length);
        d.Length.Should().BeGreaterThanOrEqualTo(1);
        d.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Unbalanced_braces_are_flagged()
    {
        ScriptDiagnostics.Analyze("function f() {").Should().NotBeEmpty();
    }

    [Fact]
    public void Offset_points_into_the_source_for_a_later_line()
    {
        const string code = "var a = 1;\nvar b = 2;\nvar c = ;";
        var diags = ScriptDiagnostics.Analyze(code);
        diags.Should().HaveCount(1);
        diags[0].Line.Should().Be(3);
        diags[0].Offset.Should().BeGreaterThan(code.IndexOf("var c", System.StringComparison.Ordinal));
    }
}
