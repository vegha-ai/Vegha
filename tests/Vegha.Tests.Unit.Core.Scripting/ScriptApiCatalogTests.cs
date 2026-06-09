using System.Reflection;
using Vegha.Core.Scripting;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class ScriptApiCatalogTests
{
    // Public members on the API classes that are host infrastructure, not part of the JS surface,
    // so they're intentionally absent from autocomplete.
    private static readonly Dictionary<string, HashSet<string>> InfraAllowList = new(StringComparer.Ordinal)
    {
        ["bru"] = new(StringComparer.Ordinal) { "RunState", "EnvVarMutations" },
        // RequestApi exposes PascalCase .NET properties for the host; scripts use the get*/set* methods.
        ["req"] = new(StringComparer.Ordinal) { "Method", "Url", "Body", "Name", "Headers", "PathParams" },
        ["res"] = new(StringComparer.Ordinal),
    };

    [Theory]
    [InlineData(typeof(BruApi), "bru")]
    [InlineData(typeof(RequestApi), "req")]
    [InlineData(typeof(ResponseApi), "res")]
    public void Catalog_covers_every_public_api_member(System.Type apiType, string objectName)
    {
        var obj = ScriptApiCatalog.AllObjects[objectName];
        var catalogNames = obj.Members.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var allow = InfraAllowList[objectName];

        var reflected = apiType
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => (m is MethodInfo mi && !mi.IsSpecialName) || m is PropertyInfo)
            .Select(m => m.Name)
            .Where(n => !allow.Contains(n))
            .Distinct();

        reflected.Should().OnlyContain(n => catalogNames.Contains(n),
            "every script-visible public member of {0} must appear in the '{1}' autocomplete catalog",
            apiType.Name, objectName);
    }

    [Fact]
    public void PreRequest_excludes_response_and_test_globals()
    {
        var names = ScriptApiCatalog.TopLevel(ScriptKind.PreRequest).Select(o => o.Name).ToList();
        names.Should().Contain(new[] { "bru", "req", "console", "_", "axios" });
        names.Should().NotContain(new[] { "res", "test", "expect" });
    }

    [Theory]
    [InlineData(ScriptKind.PostResponse)]
    [InlineData(ScriptKind.Tests)]
    public void Post_and_tests_include_response_and_test_globals(ScriptKind kind)
    {
        var names = ScriptApiCatalog.TopLevel(kind).Select(o => o.Name).ToList();
        names.Should().Contain(new[] { "bru", "req", "res", "test", "expect", "console", "_", "axios" });
    }

    [Fact]
    public void Resolve_member_access_walks_nested_objects()
    {
        ScriptApiCatalog.Resolve("bru", ScriptKind.PreRequest)!.Name.Should().Be("bru");
        ScriptApiCatalog.Resolve("bru.runner", ScriptKind.PreRequest)!.Name.Should().Be("bru.runner");
        ScriptApiCatalog.Resolve("bru.utils", ScriptKind.Tests)!.Name.Should().Be("bru.utils");
    }

    [Fact]
    public void Resolve_handles_expect_call_chain()
    {
        ScriptApiCatalog.Resolve("expect(res.getStatus())", ScriptKind.Tests)!.Name.Should().Be("Expectation");
        ScriptApiCatalog.Resolve("expect(x).to", ScriptKind.Tests)!.Name.Should().Be("ChaiChain");
        ScriptApiCatalog.Resolve("expect(x).to.be", ScriptKind.Tests)!.Name.Should().Be("ChaiChain");
    }

    [Fact]
    public void Resolve_returns_null_for_out_of_scope_head()
    {
        // res isn't in scope in a pre-request script.
        ScriptApiCatalog.Resolve("res", ScriptKind.PreRequest).Should().BeNull();
        ScriptApiCatalog.Resolve("expect(x)", ScriptKind.PreRequest).Should().BeNull();
        ScriptApiCatalog.Resolve("unknownVar", ScriptKind.Tests).Should().BeNull();
    }
}
