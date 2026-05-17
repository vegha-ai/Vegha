using Vegha.Core.Scripting;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class PostResponseTests
{
    private readonly JintHost _host = new();
    private static readonly Dictionary<string, string> NoVars = new();

    private static ResponseApi MakeResponse(int code = 200, string body = "{\"id\":42}") =>
        new(code, code == 200 ? "OK" : "Other", body, 137,
            new[]
            {
                new KeyValuePair<string, string>("Content-Type", "application/json"),
                new KeyValuePair<string, string>("X-Trace", "abc")
            });

    [Fact]
    public void NoScripts_ReturnsSuccess_NoOutcomes()
    {
        var r = _host.RunPostResponse(null, null, MakeResponse(), NoVars);
        r.IsSuccess.Should().BeTrue();
        r.TestOutcomes.Should().BeEmpty();
    }

    [Fact]
    public void Test_Pass_RecordedAsPassed()
    {
        var r = _host.RunPostResponse(null,
            """
            test('status is 200', function() {
                expect(res.status).toBe(200);
            });
            """,
            MakeResponse(),
            NoVars);

        r.IsSuccess.Should().BeTrue();
        r.TestOutcomes.Should().ContainSingle();
        var t = r.TestOutcomes[0];
        t.Name.Should().Be("status is 200");
        t.Passed.Should().BeTrue();
        t.FailureMessage.Should().BeNull();
    }

    [Fact]
    public void Test_Fail_CapturesFailureMessage_ScriptStillSucceeds()
    {
        var r = _host.RunPostResponse(null,
            """
            test('status is 999', function() {
                expect(res.status).toBe(999);
            });
            """,
            MakeResponse(),
            NoVars);

        r.IsSuccess.Should().BeTrue(); // script ran fine; only the test inside failed
        r.TestOutcomes.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Name = "status is 999",
                Passed = false,
            }, opt => opt.ExcludingMissingMembers());
        r.TestOutcomes[0].FailureMessage.Should().Contain("200").And.Contain("999");
    }

    [Fact]
    public void MultipleTests_AllRecorded()
    {
        var r = _host.RunPostResponse(null,
            """
            test('status', function() { expect(res.status).toBe(200); });
            test('body has id', function() { expect(res.body).toContain('id'); });
            test('header set', function() { expect(res.getHeader('X-Trace')).toBe('abc'); });
            test('time fast', function() { expect(res.responseTime).toBeLessThan(500); });
            """,
            MakeResponse(),
            NoVars);

        r.TestOutcomes.Should().HaveCount(4);
        r.TestOutcomes.All(t => t.Passed).Should().BeTrue();
    }

    [Fact]
    public void PostScript_CanSetVar_FromResponseBody()
    {
        // Realistic case: extract a token from response and stash for next request.
        var r = _host.RunPostResponse(
            "var data = JSON.parse(res.body); bru.setVar('userId', data.id.toString());",
            null,
            MakeResponse(),
            NoVars);

        r.IsSuccess.Should().BeTrue();
        r.RuntimeVariables.Should().ContainKey("userId").WhoseValue.Should().Be("42");
    }

    [Fact]
    public void Expect_ToContain_Substring()
    {
        var r = _host.RunPostResponse(null,
            "test('contains', function() { expect(res.body).toContain('id'); });",
            MakeResponse(body: "{\"id\":1,\"name\":\"x\"}"),
            NoVars);
        r.TestOutcomes[0].Passed.Should().BeTrue();
    }

    [Fact]
    public void Expect_ToBeGreaterThan_Number()
    {
        var r = _host.RunPostResponse(null,
            "test('time positive', function() { expect(res.responseTime).toBeGreaterThan(0); });",
            MakeResponse(),
            NoVars);
        r.TestOutcomes[0].Passed.Should().BeTrue();
    }

    [Fact]
    public void Expect_ToBeNull()
    {
        var r = _host.RunPostResponse(null,
            "test('absent header is null', function() { expect(res.getHeader('not-there')).toBeNull(); });",
            MakeResponse(),
            NoVars);
        r.TestOutcomes[0].Passed.Should().BeTrue();
    }

    [Fact]
    public void ThrowingScript_FailsResult_NoSilentSwallow()
    {
        var r = _host.RunPostResponse(
            "throw new Error('boom');",
            null,
            MakeResponse(),
            NoVars);

        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("boom");
    }

    [Fact]
    public void Test_NestedExpects_FirstFailureWins()
    {
        var r = _host.RunPostResponse(null,
            """
            test('multi-expect', function() {
                expect(res.status).toBe(200);
                expect(res.status).toBe(999);   // fails here
                expect(res.status).toBe(201);   // not reached
            });
            """,
            MakeResponse(),
            NoVars);

        r.TestOutcomes[0].Passed.Should().BeFalse();
        r.TestOutcomes[0].FailureMessage.Should().Contain("999");
    }

    [Fact]
    public void NotModifier_NegatesAllMatchers()
    {
        var r = _host.RunPostResponse(null,
            """
            test('not.toBe pass', function() { expect(res.status).not.toBe(404); });
            test('not.toBe fail', function() { expect(res.status).not.toBe(200); });
            test('not.toContain pass', function() { expect(res.body).not.toContain('zzz'); });
            test('not.toBeTruthy fail', function() { expect(true).not.toBeTruthy(); });
            """,
            MakeResponse(),
            NoVars);

        r.TestOutcomes.Select(o => (o.Name, o.Passed)).Should().BeEquivalentTo(new[]
        {
            ("not.toBe pass", true),
            ("not.toBe fail", false),
            ("not.toContain pass", true),
            ("not.toBeTruthy fail", false),
        });
    }

    [Fact]
    public void NumericComparators_CoverInclusiveBounds()
    {
        var r = _host.RunPostResponse(null,
            """
            test('gte equal', function() { expect(res.status).toBeGreaterThanOrEqual(200); });
            test('gte greater', function() { expect(res.status).toBeGreaterThanOrEqual(100); });
            test('lte equal', function() { expect(res.status).toBeLessThanOrEqual(200); });
            test('lte less', function() { expect(res.status).toBeLessThanOrEqual(500); });
            test('gte fail', function() { expect(res.status).toBeGreaterThanOrEqual(999); });
            """,
            MakeResponse(),
            NoVars);

        r.TestOutcomes.Take(4).All(o => o.Passed).Should().BeTrue();
        r.TestOutcomes[4].Passed.Should().BeFalse();
    }

    [Fact]
    public void ToMatch_RegexAcrossString()
    {
        var r = _host.RunPostResponse(null,
            """
            test('match pass', function() { expect(res.body).toMatch('"id":\\s*\\d+'); });
            test('match fail', function() { expect(res.body).toMatch('zzz'); });
            test('not.match pass', function() { expect(res.body).not.toMatch('zzz'); });
            """,
            MakeResponse(),
            NoVars);

        r.TestOutcomes.Select(o => o.Passed).Should().BeEquivalentTo(new[] { true, false, true });
    }

    [Fact]
    public void Defined_Undefined_AndChaining()
    {
        var r = _host.RunPostResponse(null,
            """
            test('defined', function() { expect(res.status).toBeDefined(); });
            test('undefined fail', function() { expect(res.status).toBeUndefined(); });
            test('null pass', function() { expect(null).toBeNull(); });
            """,
            MakeResponse(),
            NoVars);

        r.TestOutcomes.Select(o => o.Passed).Should().BeEquivalentTo(new[] { true, false, true });
    }
}
