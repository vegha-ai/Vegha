using System.Globalization;
using Vegha.Core.Interpolation;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Interpolation;

/// <summary>Sanity tests over <see cref="DynamicVariableProvider"/> + its wiring into
/// <see cref="Interpolator.Resolve(string, System.Collections.Generic.IReadOnlyDictionary{string,string})"/>.
/// One per "shape" — we don't pin random output, just verify shape + non-emptiness.</summary>
public class DynamicVariableTests
{
    private static readonly IReadOnlyDictionary<string, string> NoVars =
        new Dictionary<string, string>();

    [Fact]
    public void RandomUUID_IsAParseableGuid()
    {
        var rendered = Interpolator.Resolve("{{$randomUUID}}", NoVars);
        Guid.TryParse(rendered, out _).Should().BeTrue();
    }

    [Fact]
    public void Timestamp_IsAUnixTimeWithinSensibleRange()
    {
        var rendered = Interpolator.Resolve("{{$timestamp}}", NoVars);
        long.TryParse(rendered, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix).Should().BeTrue();
        unix.Should().BeGreaterThan(1_700_000_000L); // ~Nov 2023 onwards
    }

    [Fact]
    public void IsoTimestamp_ParsesAsDateTime()
    {
        var rendered = Interpolator.Resolve("{{$isoTimestamp}}", NoVars);
        DateTime.TryParse(rendered, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            .Should().BeTrue();
    }

    [Fact]
    public void RandomInt_IsAnIntegerInRange()
    {
        for (var i = 0; i < 5; i++)
        {
            var rendered = Interpolator.Resolve("{{$randomInt}}", NoVars);
            int.TryParse(rendered, out var n).Should().BeTrue();
            n.Should().BeInRange(0, 1000);
        }
    }

    [Fact]
    public void RandomBoolean_IsTrueOrFalse()
    {
        var rendered = Interpolator.Resolve("{{$randomBoolean}}", NoVars);
        rendered.Should().BeOneOf("true", "false");
    }

    [Fact]
    public void RandomFullName_HasTwoSpaceSeparatedTokens()
    {
        var rendered = Interpolator.Resolve("{{$randomFullName}}", NoVars);
        rendered.Split(' ').Length.Should().Be(2);
    }

    [Fact]
    public void RandomEmail_LooksLikeAnEmail()
    {
        var rendered = Interpolator.Resolve("{{$randomEmail}}", NoVars);
        rendered.Should().Contain("@");
        rendered.Should().Contain(".");
    }

    [Fact]
    public void RandomIp_IsFourDottedOctets()
    {
        var rendered = Interpolator.Resolve("{{$randomIP}}", NoVars);
        var parts = rendered.Split('.');
        parts.Length.Should().Be(4);
        foreach (var p in parts) int.TryParse(p, out _).Should().BeTrue();
    }

    [Fact]
    public void UnknownDollarVar_IsLeftLiteral()
    {
        // Unknown dynamic name should NOT be eagerly replaced — better to leave the
        // placeholder visible so the user sees what's missing.
        var rendered = Interpolator.Resolve("{{$thisIsNotAThing}}", NoVars);
        rendered.Should().Be("{{$thisIsNotAThing}}");
    }

    [Fact]
    public void DynamicVar_CoexistsWithDictionaryVars()
    {
        var vars = new Dictionary<string, string> { ["baseUrl"] = "https://x.test" };
        var rendered = Interpolator.Resolve("{{baseUrl}}/users/{{$randomUUID}}", vars);
        rendered.Should().StartWith("https://x.test/users/");
        // Trailing GUID
        Guid.TryParse(rendered.AsSpan("https://x.test/users/".Length), out _).Should().BeTrue();
    }
}
