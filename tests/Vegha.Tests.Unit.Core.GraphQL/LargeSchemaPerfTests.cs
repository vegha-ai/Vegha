using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Vegha.Core.GraphQL.Editor;
using Vegha.Core.GraphQL.Schema;
using Xunit;

namespace Vegha.Tests.Unit.Core.GraphQL;

/// <summary>
/// Budget guards for GitHub-scale schemas (thousands of types). Generated fixture:
/// 1200 object types × 15 fields + 200 enums + 100 inputs ≈ 20k fields. Budgets are
/// deliberately loose (CI machines vary) — they catch algorithmic regressions
/// (accidental O(n²)), not micro-slowdowns.
/// </summary>
public class LargeSchemaPerfTests
{
    private static readonly Lazy<string> LargeIntrospectionJson = new(GenerateLargeIntrospection);
    private static readonly Lazy<GraphQLSchemaModel> LargeSchema =
        new(() => IntrospectionJsonReader.Parse(LargeIntrospectionJson.Value));

    [Fact]
    public void Reader_ParsesLargeSchema_WithinBudget()
    {
        var sw = Stopwatch.StartNew();
        var schema = IntrospectionJsonReader.Parse(LargeIntrospectionJson.Value);
        sw.Stop();

        schema.Types.Count.Should().BeGreaterThan(1400);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            $"parsing ~{schema.Types.Count} types took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Completion_WarmLookup_IsFast()
    {
        var schema = LargeSchema.Value;
        var doc = "query Q { type0500 { field003 } type0800 { ";
        var caret = doc.Length;

        // Warm-up (JIT + frozen dictionary materialization already done by Lazy).
        GraphQLCompletionEngine.GetCompletions(doc, caret, schema);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
            GraphQLCompletionEngine.GetCompletions(doc, caret, schema);
        sw.Stop();

        var perCall = sw.Elapsed.TotalMilliseconds / 50;
        perCall.Should().BeLessThan(10, $"warm completion took {perCall:0.00} ms/call");
    }

    [Fact]
    public void SdlRender_LargeSchema_WithinBudget()
    {
        var sw = Stopwatch.StartNew();
        var sdl = SdlRenderer.Render(LargeSchema.Value);
        sw.Stop();

        sdl.Length.Should().BeGreaterThan(500_000);
        sw.ElapsedMilliseconds.Should().BeLessThan(3000);
    }

    private static string GenerateLargeIntrospection()
    {
        var sb = new StringBuilder(16 * 1024 * 1024);
        sb.Append("""{ "data": { "__schema": { "queryType": { "name": "Query" }, "types": [""");

        // Root type whose fields point at every object type.
        sb.Append("""{ "kind": "OBJECT", "name": "Query", "fields": [""");
        for (var t = 0; t < 1200; t++)
        {
            if (t > 0) sb.Append(',');
            sb.Append($$"""{ "name": "type{{t:0000}}", "args": [], "type": { "kind": "OBJECT", "name": "Type{{t:0000}}" } }""");
        }
        sb.Append("] },");

        for (var t = 0; t < 1200; t++)
        {
            sb.Append($$"""{ "kind": "OBJECT", "name": "Type{{t:0000}}", "description": "Generated type {{t}}", "fields": [""");
            for (var f = 0; f < 15; f++)
            {
                if (f > 0) sb.Append(',');
                sb.Append($$"""{ "name": "field{{f:000}}", "description": "Field {{f}}", "args": [ { "name": "first", "type": { "kind": "SCALAR", "name": "Int" } } ], "type": { "kind": "NON_NULL", "name": null, "ofType": { "kind": "SCALAR", "name": "String" } } }""");
            }
            sb.Append("] },");
        }

        for (var e = 0; e < 200; e++)
        {
            sb.Append($$"""{ "kind": "ENUM", "name": "Enum{{e:000}}", "enumValues": [ { "name": "A" }, { "name": "B" }, { "name": "C" } ] },""");
        }
        for (var i = 0; i < 100; i++)
        {
            sb.Append($$"""{ "kind": "INPUT_OBJECT", "name": "Input{{i:000}}", "inputFields": [ { "name": "x", "type": { "kind": "SCALAR", "name": "Int" } } ] },""");
        }
        sb.Append("""{ "kind": "SCALAR", "name": "String" }, { "kind": "SCALAR", "name": "Int" }""");
        sb.Append("] } } }");
        return sb.ToString();
    }
}
