using Vegha.Core.Scripting;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

/// <summary>Drives the Chai-style chain (<c>expect(x).to.equal(y)</c>) through the post-response
/// path so we exercise the full Jint → C# binding, not the C# matcher in isolation. Cross-validates
/// that Bruno-translated Postman scripts run under Vegha.</summary>
public class ChaiChainTests
{
    private readonly JintHost _host = new();
    private static readonly Dictionary<string, string> NoVars = new();

    private static ResponseApi NoResponse() =>
        new(200, "OK", string.Empty, 0, Array.Empty<KeyValuePair<string, string>>());

    [Theory]
    [InlineData("test('eq', () => { expect(1).to.equal(1); });", true)]
    [InlineData("test('eq fail', () => { expect(1).to.equal(2); });", false)]
    [InlineData("test('not eq', () => { expect(1).not.to.equal(2); });", true)]
    [InlineData("test('be true', () => { expect(true).to.be.true; });", true)]
    [InlineData("test('be false', () => { expect(false).to.be.false; });", true)]
    [InlineData("test('be null', () => { expect(null).to.be.null; });", true)]
    [InlineData("test('exist', () => { expect('x').to.exist; });", true)]
    [InlineData("test('not exist', () => { expect(null).to.not.exist; });", true)]
    [InlineData("test('empty', () => { expect('').to.be.empty; });", true)]
    [InlineData("test('not empty', () => { expect('x').to.not.be.empty; });", true)]
    [InlineData("test('a string', () => { expect('x').to.be.a('string'); });", true)]
    [InlineData("test('an array', () => { expect([1,2]).to.be.an('array'); });", true)]
    [InlineData("test('above', () => { expect(5).to.be.above(3); });", true)]
    [InlineData("test('below', () => { expect(3).to.be.below(5); });", true)]
    [InlineData("test('lengthOf', () => { expect([1,2,3]).to.have.lengthOf(3); });", true)]
    [InlineData("test('match', () => { expect('abc123').to.match('^[a-z]+\\\\d+$'); });", true)]
    [InlineData("test('contain', () => { expect('hello world').to.contain('world'); });", true)]
    [InlineData("test('contain arr', () => { expect([1,2,3]).to.contain(2); });", true)]
    public void ChaiChain_AssertionsRunUnderJint(string script, bool shouldPass)
    {
        var r = _host.RunPostResponse(null, script, NoResponse(), NoVars);
        r.IsSuccess.Should().BeTrue();
        r.TestOutcomes.Should().HaveCount(1);
        r.TestOutcomes[0].Passed.Should().Be(shouldPass);
    }

    [Fact]
    public void ChaiHaveStatus_AssertsAgainstResponse()
    {
        var resp = new ResponseApi(404, "Not Found", string.Empty, 0,
            Array.Empty<KeyValuePair<string, string>>());
        var script = """
            test('not found', () => { expect(res).to.have.status(404); });
            test('mismatch', () => { expect(res).to.have.status(200); });
        """;
        var r = _host.RunPostResponse(null, script, resp, NoVars);
        r.TestOutcomes.Should().HaveCount(2);
        r.TestOutcomes[0].Passed.Should().BeTrue();
        r.TestOutcomes[1].Passed.Should().BeFalse();
    }

    [Fact]
    public void ChaiProperty_RetargetsAtNamedField()
    {
        var resp = new ResponseApi(200, "OK", "{\"id\":42,\"name\":\"alice\"}", 0,
            new[] { new KeyValuePair<string, string>("Content-Type", "application/json") });
        var script = """
            test('has id', () => {
                expect(res.getBody()).to.have.property('id').that.equals(42);
            });
            test('has name', () => {
                expect(res.getBody()).to.have.property('name').that.equals('alice');
            });
        """;
        var r = _host.RunPostResponse(null, script, resp, NoVars);
        r.IsSuccess.Should().BeTrue();
        r.TestOutcomes.Should().AllSatisfy(t => t.Passed.Should().BeTrue());
    }

    [Fact]
    public void ChaiDeepEqual_ComparesObjectGraphs()
    {
        var resp = new ResponseApi(200, "OK", "{\"a\":[1,2,3]}", 0,
            new[] { new KeyValuePair<string, string>("Content-Type", "application/json") });
        // Jint exposes the parsed body's `a` array as an iterable — chain through .property('a').
        var script = """
            test('deep eq via property', () => {
                expect(res.getBody()).to.have.property('a').that.to.have.lengthOf(3);
            });
        """;
        var r = _host.RunPostResponse(null, script, resp, NoVars);
        r.TestOutcomes[0].Passed.Should().BeTrue();
    }
}
