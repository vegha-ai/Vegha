using FluentAssertions;
using Vegha.App.ViewModels;
using Vegha.Core.GraphQL.Schema;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class GraphQLQueryBuilderViewModelTests
{
    private const string Fixture = """
    { "data": { "__schema": {
      "queryType": { "name": "Query" },
      "mutationType": { "name": "Mutation" },
      "types": [
        { "kind": "OBJECT", "name": "Query",
          "fields": [
            { "name": "continent",
              "args": [ { "name": "code", "type": { "kind": "NON_NULL", "name": null, "ofType": { "kind": "SCALAR", "name": "ID" } } } ],
              "type": { "kind": "OBJECT", "name": "Continent" } },
            { "name": "ping", "args": [], "type": { "kind": "SCALAR", "name": "String" } }
          ] },
        { "kind": "OBJECT", "name": "Mutation",
          "fields": [ { "name": "reset", "args": [], "type": { "kind": "SCALAR", "name": "Boolean" } } ] },
        { "kind": "OBJECT", "name": "Continent",
          "fields": [
            { "name": "name", "args": [ { "name": "lang", "type": { "kind": "SCALAR", "name": "String" } } ], "type": { "kind": "SCALAR", "name": "String" } },
            { "name": "countries", "args": [], "type": { "kind": "LIST", "name": null, "ofType": { "kind": "OBJECT", "name": "Country" } } }
          ] },
        { "kind": "OBJECT", "name": "Country",
          "fields": [
            { "name": "code", "args": [], "type": { "kind": "SCALAR", "name": "ID" } },
            { "name": "capital", "args": [], "type": { "kind": "SCALAR", "name": "String" } }
          ] }
      ]
    } } }
    """;

    private static (GraphQLQueryBuilderViewModel Builder, List<string> Generated) Create(string initialQuery = "")
    {
        var builder = new GraphQLQueryBuilderViewModel();
        var generated = new List<string>();
        builder.QueryRegenerated += generated.Add;
        builder.SetSchema(IntrospectionJsonReader.Parse(Fixture), initialQuery);
        return (builder, generated);
    }

    private static BuilderFieldViewModel Field(GraphQLQueryBuilderViewModel b, params string[] path)
    {
        var fields = b.Roots.First(r => r.Label == "Query").Fields();
        BuilderFieldViewModel? current = null;
        foreach (var name in path)
        {
            current = fields.First(f => f.Name == name);
            current.MaterializeChildren();
            fields = current.MaterializedFields();
        }
        return current!;
    }

    [Fact]
    public void Roots_BuiltFromSchema_QueryAndMutation()
    {
        var (b, _) = Create();
        b.Roots.Select(r => r.Label).Should().Equal("Query", "Mutation");
        b.Roots[0].Fields().Select(f => f.Name).Should().Equal("continent", "ping");
    }

    [Fact]
    public void CheckingLeaf_GeneratesQuery_AndChecksAncestors()
    {
        var (b, generated) = Create();
        var capital = Field(b, "continent", "countries", "capital");

        capital.IsChecked = true;

        Field(b, "continent").IsChecked.Should().BeTrue("ancestors auto-check");
        Field(b, "continent", "countries").IsChecked.Should().BeTrue();
        generated.Last().Should().Contain("continent");
        generated.Last().Should().Contain("countries {");
        generated.Last().Should().Contain("capital");
    }

    [Fact]
    public void ArgValue_String_AutoQuoted_IdNumeric_Verbatim()
    {
        var (b, generated) = Create();
        var continent = Field(b, "continent");
        continent.IsChecked = true;
        continent.Args[0].Value = "EU";
        generated.Last().Should().Contain("continent(code: \"EU\")", "non-numeric ID input auto-quotes");

        var name = Field(b, "continent", "name");
        name.IsChecked = true;
        name.Args[0].Value = "\"fr\"";
        generated.Last().Should().Contain("name(lang: \"fr\")", "pre-quoted input passes verbatim");
    }

    [Fact]
    public void UncheckingParent_ClearsDescendants()
    {
        var (b, generated) = Create();
        Field(b, "continent", "countries", "capital").IsChecked = true;
        Field(b, "continent").IsChecked = false;

        Field(b, "continent", "countries", "capital").IsChecked.Should().BeFalse();
        generated.Last().Trim().Should().BeEmpty("nothing selected → empty document");
    }

    [Fact]
    public void CheckedComposite_WithNoChildren_EmitsEmptyBraces()
    {
        var (b, generated) = Create();
        Field(b, "continent").IsChecked = true;
        generated.Last().Should().Contain("continent {");
    }

    [Fact]
    public void MutationField_GeneratesMutationOperation()
    {
        var (b, generated) = Create();
        var reset = b.Roots.First(r => r.Label == "Mutation").Fields().First(f => f.Name == "reset");
        reset.IsChecked = true;
        generated.Last().Should().Contain("mutation {");
        generated.Last().Should().Contain("reset");
    }

    [Fact]
    public void SyncFromQuery_ReflectsTextIntoTree()
    {
        var (b, _) = Create();
        b.SyncFromQuery("""
            query Trip {
              continent(code: "AF") {
                countries {
                  code
                }
              }
            }
            """);

        var continent = Field(b, "continent");
        continent.IsChecked.Should().BeTrue();
        continent.Args[0].Value.Should().Be("\"AF\"");
        Field(b, "continent", "countries", "code").IsChecked.Should().BeTrue();
        Field(b, "continent", "countries", "capital").IsChecked.Should().BeFalse();
        b.Roots[0].OperationName.Should().Be("Trip");
        b.ReadOnlyReason.Should().BeNull();
    }

    [Fact]
    public void SyncFromQuery_PreservesOperationName_OnRegenerate()
    {
        var (b, generated) = Create("query Trip { ping }");
        Field(b, "ping").IsChecked.Should().BeTrue();

        Field(b, "continent").IsChecked = true;
        generated.Last().Should().Contain("query Trip {");
    }

    [Fact]
    public void SyncFromQuery_Fragments_FlipReadOnly_CheckboxesInert()
    {
        var (b, generated) = Create();
        b.SyncFromQuery("query Q { continent(code: \"EU\") { ...Fields } } fragment Fields on Continent { name }");

        b.ReadOnlyReason.Should().Contain("fragments");
        var before = generated.Count;
        Field(b, "ping").IsChecked = true;
        generated.Count.Should().Be(before, "read-only builder must not regenerate (and destroy) the document");
    }

    [Fact]
    public void SyncFromQuery_SyntaxError_KeepsTreeAndStaysEditable()
    {
        var (b, _) = Create("query { ping }");
        Field(b, "ping").IsChecked.Should().BeTrue();

        b.SyncFromQuery("query { ping {"); // mid-typing
        Field(b, "ping").IsChecked.Should().BeTrue("transient syntax errors must not reset the tree");
        b.ReadOnlyReason.Should().BeNull();
    }

    [Fact]
    public void BuilderGeneratedText_DoesNotBounceBack()
    {
        var (b, generated) = Create();
        Field(b, "ping").IsChecked = true;
        var text = generated.Last();

        // The editor echoes the generated text into SyncFromQuery — must be a no-op.
        b.SyncFromQuery(text);
        Field(b, "ping").IsChecked.Should().BeTrue();
        generated.Count.Should().Be(1);
    }

    [Fact]
    public void Search_FiltersMaterializedRows()
    {
        var (b, _) = Create();
        Field(b, "continent", "countries"); // materialize the branch
        b.SearchText = "capital";
        // Debounce-free path: filter applies synchronously on property change.
        Field(b, "continent").IsVisible.Should().BeTrue("ancestor of a match stays visible");
        Field(b, "ping").IsVisible.Should().BeFalse();
        b.SearchText = "";
        Field(b, "ping").IsVisible.Should().BeTrue();
    }
}
