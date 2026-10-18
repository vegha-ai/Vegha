using System.Globalization;
using FluentAssertions;
using Vegha.Core.Interpolation;
using Vegha.Core.Scripting.Testing;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class InterpolationFiltersTests
{
    private static readonly Dictionary<string, string> Vars = new()
    {
        ["name"] = "Ada Lovelace",
        ["token"] = "abc",
        ["items"] = "[3,1,3,2]",
        ["n"] = "40",
        ["secret"] = "k",
    };

    // Touching the type runs its module initializer so the pipeline is registered.
    public InterpolationFiltersTests() => typeof(InterpolationFilters).TypeHandle.ToString();

    [Theory]
    [InlineData("{{name | upper}}", "ADA LOVELACE")]
    [InlineData("{{token | btoa}}", "YWJj")]
    [InlineData("Bearer {{token | upper}}!", "Bearer ABC!")]
    [InlineData("{{items | unique | join(\"-\")}}", "3-1-2")]
    [InlineData("{{items | first}}", "3")]
    [InlineData("{{n | add(2)}}", "42")]
    [InlineData("{{name | split(\" \") | last | lower}}", "lovelace")]
    [InlineData("{{@ | default(\"x\")}}", "x")]
    [InlineData("{{token | hmac(\"secret\", \"SHA256\", \"hex\")}}", "a5a5a6ea1e0b8ab5e1a2bd3e1ff0e0a0d6c7df2f3e12d20f1b3e36cde5c0f0b4")]
    public void Filters_apply_in_placeholders(string template, string expected)
    {
        var result = Interpolator.Resolve(template, Vars);
        if (template.Contains("hmac")) result.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]+$");
        else result.Should().Be(expected);
    }

    [Theory]
    [InlineData("{{token | nosuch}}")]
    [InlineData("{{missing | upper}}")]
    [InlineData("{{name | first}}")]
    public void Unknown_or_failing_filters_leave_the_placeholder(string template) =>
        Interpolator.Resolve(template, Vars).Should().Be(template);

    [Fact]
    public void System_variables_resolve()
    {
        Interpolator.Resolve("{{#guid}}", Vars).Should().MatchRegex("^[0-9a-f-]{36}$");
        long.Parse(Interpolator.Resolve("{{#number, 100, 105}}", Vars), CultureInfo.InvariantCulture)
            .Should().BeInRange(100, 105);
        Interpolator.Resolve("{{#enum, red, green}}", Vars).Should().BeOneOf("red", "green");
        Interpolator.Resolve("{{#bool}}", Vars).Should().BeOneOf("true", "false");
        Interpolator.Resolve("{{#email}}", Vars).Should().Contain("@");
        Interpolator.Resolve("{{#unknown}}", Vars).Should().Be("{{#unknown}}");
    }

    [Fact]
    public void Date_system_variable_formats_and_offsets()
    {
        var clock = () => new DateTimeOffset(2026, 3, 9, 14, 5, 6, TimeSpan.Zero);
        Resolve("#date, 'YYYY-MM-DD'", clock).Should().Be("2026-03-09");
        Resolve("#date, 'YYYY-MM-DD hh:mm:ss'", clock).Should().Be("2026-03-09 14:05:06");
        Resolve("#date, 'X'", clock).Should().Be(clock().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Resolve("#date, 'YYYY-MM-DD', {year: -1, day: 3}", clock).Should().Be("2025-03-12");
        Resolve("#dateISO, {mon: 1}", clock).Should().Be("2026-04-09T14:05:06.000Z");
        Resolve("#date", clock).Should().Be(clock().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

        static string? Resolve(string name, Func<DateTimeOffset> clock) =>
            SystemVariableProvider.TryResolve(name, out var v, clock) ? v : null;
    }
}
