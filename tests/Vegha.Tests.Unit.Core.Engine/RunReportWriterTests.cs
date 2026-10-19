using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Vegha.Core.Engine.Reports;
using Xunit;

namespace Vegha.Tests.Unit.Core.Engine;

public class RunReportWriterTests
{
    private static RunReport Sample(int iterations = 1) => new(
        "Pet <Store>",
        new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        1234,
        iterations,
        new[]
        {
            new RunReportEntry("list", "Pets", 0, "GET", "https://x/pets?a=1&b=2", 200, 40, false, false, null,
                new[] { new RunReportTest("status is 200", true, null) }),
            new RunReportEntry("create", "Pets", 0, "POST", "https://x/pets", 500, 90, true, false, "1/1 tests failed",
                new[] { new RunReportTest("status is 201", false, "expected 500 to be 201") }),
        });

    [Fact]
    public void JUnit_counts_failures_and_includes_failing_check_text()
    {
        var doc = XDocument.Parse(RunReportWriter.JUnit(Sample()));

        var suite = doc.Root!.Element("testsuite")!;
        suite.Attribute("tests")!.Value.Should().Be("2");
        suite.Attribute("failures")!.Value.Should().Be("1");
        var failure = suite.Elements("testcase").Single(t => t.Attribute("name")!.Value == "create").Element("failure")!;
        failure.Attribute("message")!.Value.Should().Be("1/1 tests failed");
        failure.Value.Should().Contain("status is 201: expected 500 to be 201");
    }

    [Fact]
    public void JUnit_suffixes_iteration_only_for_multi_iteration_runs()
    {
        XDocument.Parse(RunReportWriter.JUnit(Sample(iterations: 1))).Descendants("testcase")
            .Select(t => t.Attribute("name")!.Value).Should().Equal("list", "create");
        XDocument.Parse(RunReportWriter.JUnit(Sample(iterations: 2))).Descendants("testcase")
            .Select(t => t.Attribute("name")!.Value).Should().Equal("list [iteration 1]", "create [iteration 1]");
    }

    [Fact]
    public void Json_has_summary_and_camel_case_results()
    {
        using var doc = JsonDocument.Parse(RunReportWriter.Json(Sample()));
        var root = doc.RootElement;
        root.GetProperty("summary").GetProperty("failed").GetInt32().Should().Be(1);
        root.GetProperty("results")[1].GetProperty("statusCode").GetInt32().Should().Be(500);
        root.GetProperty("results")[1].GetProperty("tests")[0].GetProperty("failureMessage").GetString()
            .Should().Be("expected 500 to be 201");
    }

    [Fact]
    public void Html_encodes_user_content_and_has_no_scripts()
    {
        var html = RunReportWriter.Html(Sample());
        html.Should().Contain("Pet &lt;Store&gt;").And.Contain("a=1&amp;b=2").And.NotContain("<script");
        html.Should().Contain("prefers-color-scheme:dark");
    }

    [Fact]
    public void Unknown_format_is_rejected_with_supported_list()
    {
        var act = () => RunReportWriter.Render(Sample(), "pdf");
        act.Should().Throw<ArgumentException>().WithMessage("*junit, json, html*");
    }
}
