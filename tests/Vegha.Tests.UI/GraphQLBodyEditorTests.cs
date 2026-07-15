using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>Headless coverage for the GraphQL body editor pane: lazy xshd registration,
/// theme-name mapping, and the operation picker's visibility contract.</summary>
public class GraphQLBodyEditorTests
{
    private static RequestEditorViewModel CreateVm()
    {
        var http = new HttpClient();
        return new RequestEditorViewModel(
            new HttpExecutor(http),
            new OAuth2TokenAcquirer(http),
            new JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
    }

    [AvaloniaFact]
    public void GraphQLHighlighting_RegistersLazily_AndResolves()
    {
        Vegha.App.Controls.Workspace.Highlighting.GraphQLHighlighting.EnsureRegistered();
        var def = HighlightingManager.Instance.GetDefinition("GraphQL");
        def.Should().NotBeNull("the embedded GraphQL.xshd must load and register");
        def!.Name.Should().Be("GraphQL");
    }

    [AvaloniaFact]
    public void GraphQLXshd_EveryNamedColor_MapsToAThemeBrushCategory()
    {
        Vegha.App.Controls.Workspace.Highlighting.GraphQLHighlighting.EnsureRegistered();
        var def = HighlightingManager.Instance.GetDefinition("GraphQL")!;

        // Mirror of EditorSyntaxTheme.ChooseBrushKeyByCategory's match set — if a color is
        // renamed/added in the xshd without a category mapping it renders un-themed.
        string[] recognized =
        {
            "comment", "string", "digit", "number", "bool", "null", "keyword",
            "directive", "gqlvar", "typename", "punctuation", "symbol", "operator",
        };
        foreach (var color in def.NamedHighlightingColors)
        {
            var n = color.Name.ToLowerInvariant();
            recognized.Any(r => n.Contains(r)).Should().BeTrue(
                $"xshd color '{color.Name}' must match an EditorSyntaxTheme category so it re-themes");
        }
    }

    private const string SchemaFixture = """
    { "data": { "__schema": {
      "queryType": { "name": "Query" },
      "types": [
        { "kind": "OBJECT", "name": "Query",
          "fields": [
            { "name": "user", "args": [ { "name": "id", "type": { "kind": "SCALAR", "name": "ID" } } ],
              "type": { "kind": "OBJECT", "name": "User" } },
            { "name": "ping", "args": [], "type": { "kind": "SCALAR", "name": "String" } }
          ] },
        { "kind": "OBJECT", "name": "User",
          "fields": [ { "name": "id", "args": [], "type": { "kind": "SCALAR", "name": "ID" } } ] }
      ]
    } } }
    """;

    [AvaloniaFact]
    public void CtrlSpace_OpensSchemaAwareCompletion_WithRootFields()
    {
        var editor = new VariableAwareTextEditor
        {
            SingleLine = false,
            CompletionMode = EditorCompletionMode.GraphQL,
            GraphQLSchema = Vegha.Core.GraphQL.Schema.IntrospectionJsonReader.Parse(SchemaFixture),
            Text = "query Q {  }",
        };
        var window = new Window { Width = 700, Height = 400, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            var inner = editor.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().First();
            inner.CaretOffset = 10; // inside the selection set
            inner.TextArea.Focus();

            // The Ctrl+Space handler is registered on the inner TextEditor — raise there.
            inner.RaiseEvent(new Avalonia.Input.KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                Key = Avalonia.Input.Key.Space,
                KeyModifiers = Avalonia.Input.KeyModifiers.Control,
                Source = inner,
            });

            var field = typeof(VariableAwareTextEditor).GetField(
                "_completionWindow",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var completion = field.GetValue(editor) as AvaloniaEdit.CodeCompletion.CompletionWindow;
            completion.Should().NotBeNull("Ctrl+Space in a selection set must open the completion window");
            var labels = completion!.CompletionList.CompletionData.Select(d => d.Text).ToList();
            labels.Should().Contain(new[] { "user", "ping", "__typename" });
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SchemaValidation_SquigglesUnknownField_AfterDebounce()
    {
        var editor = new VariableAwareTextEditor
        {
            SingleLine = false,
            CompletionMode = EditorCompletionMode.GraphQL,
            GraphQLSchema = Vegha.Core.GraphQL.Schema.IntrospectionJsonReader.Parse(SchemaFixture),
        };
        var window = new Window { Width = 700, Height = 400, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            editor.Text = "query Q { nonsense }";

            // Diagnostics are debounced (300 ms) + computed off-thread + posted back.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (editor.Diagnostics.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }

            editor.Diagnostics.Should().ContainSingle()
                .Which.Message.Should().Contain("nonsense");
            editor.HasError.Should().BeTrue();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SchemaExplorer_RendersAndNavigates_WithSchema()
    {
        var vm = CreateVm();
        vm.BodyType = "graphql";
        // Publish a schema the way introspection does (private path exercised elsewhere) —
        // drive the explorer child VM directly.
        var schema = Vegha.Core.GraphQL.Schema.IntrospectionJsonReader.Parse(SchemaFixture);
        vm.SchemaExplorer.SetSchema(schema);

        var explorer = new GraphQLSchemaExplorer { DataContext = vm.SchemaExplorer };
        var window = new Window { Width = 320, Height = 500, Content = explorer };
        window.Show();
        try
        {
            window.UpdateLayout();
            var list = explorer.GetVisualDescendants().OfType<ListBox>().First();
            list.ItemCount.Should().BeGreaterThan(2, "root page shows roots + all types");

            vm.SchemaExplorer.NavigateTo("User");
            window.UpdateLayout();
            vm.SchemaExplorer.Breadcrumb.Should().Be("Schema › User");
            list.ItemCount.Should().BeGreaterThan(0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void QueryBuilder_RendersTree_TogglingFieldWritesQuery()
    {
        var vm = CreateVm();
        vm.BodyType = "graphql";
        var schema = Vegha.Core.GraphQL.Schema.IntrospectionJsonReader.Parse(SchemaFixture);
        vm.QueryBuilder.SetSchema(schema, vm.GraphQLQuery);

        var view = new GraphQLQueryBuilderView { DataContext = vm.QueryBuilder };
        var window = new Window { Width = 360, Height = 600, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            var tree = view.GetVisualDescendants().OfType<TreeView>().First();
            tree.ItemCount.Should().Be(1, "one root group (Query)");

            // Drive the VM the way the checkbox binding would.
            var ping = vm.QueryBuilder.Roots[0].Fields().First(f => f.Name == "ping");
            ping.IsChecked = true;
            window.UpdateLayout();

            vm.GraphQLQuery.Should().Contain("ping", "checking a field must write the query text");
            vm.GraphQLQuery.Should().Contain("query {");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void QueryVariablesSplitter_DragResizesVariablesRow()
    {
        var vm = CreateVm();
        vm.BodyType = "graphql";

        var editor = new BodyEditor { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            var splitter = editor.GetVisualDescendants().OfType<GridSplitter>()
                .First(s => s.Classes.Contains("row"));
            var grid = (Grid)splitter.Parent!;
            var before = grid.RowDefinitions[2].ActualHeight;

            var origin = splitter.TranslatePoint(
                new Avalonia.Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
            window.MouseDown(origin, Avalonia.Input.MouseButton.Left);
            window.MouseMove(new Avalonia.Point(origin.X, origin.Y - 60));
            window.MouseUp(new Avalonia.Point(origin.X, origin.Y - 60), Avalonia.Input.MouseButton.Left);
            window.UpdateLayout();

            var after = grid.RowDefinitions[2].ActualHeight;
            after.Should().BeGreaterThan(before + 30,
                $"dragging the splitter up 60px must grow the variables row (before={before}, after={after})");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SubscriptionView_RendersFrames()
    {
        var vm = CreateVm();
        vm.SubscriptionFrames.Add(new GraphQLSubscriptionFrame(
            DateTimeOffset.UtcNow, "data", "{\"data\":{\"tick\":1}}", "{\"data\":{\"tick\":1}}"));
        vm.SubscriptionFrames.Add(new GraphQLSubscriptionFrame(
            DateTimeOffset.UtcNow, "error", "boom", "[{\"message\":\"boom\"}]"));

        var view = new GraphQLSubscriptionView { DataContext = vm };
        var window = new Window { Width = 700, Height = 300, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            var list = view.GetVisualDescendants().OfType<ListBox>().First();
            list.ItemCount.Should().Be(2);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void OperationPicker_HiddenForSingleOperation_VisibleForMultiple()
    {
        var vm = CreateVm();
        vm.BodyType = "graphql";

        var editor = new BodyEditor { DataContext = vm };
        var window = new Window { Width = 900, Height = 500, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            var combo = editor.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault();
            combo.Should().NotBeNull("the QUERY header hosts the operation picker ComboBox");
            combo!.IsVisible.Should().BeFalse("no operations parsed yet — picker stays hidden");

            // Simulate the debounced analyzer publishing two operations.
            vm.GraphQLOperationNames.Add("First");
            vm.GraphQLOperationNames.Add("Second");
            vm.SelectedGraphQLOperationName = "First";
            // HasMultipleGraphQLOperations is computed; nudge the binding like the VM does.
            typeof(RequestEditorViewModel)
                .GetMethod("OnPropertyChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    new[] { typeof(string) })!
                .Invoke(vm, new object[] { nameof(RequestEditorViewModel.HasMultipleGraphQLOperations) });

            window.UpdateLayout();
            combo.IsVisible.Should().BeTrue("two named operations → picker shows");
        }
        finally
        {
            window.Close();
        }
    }
}
