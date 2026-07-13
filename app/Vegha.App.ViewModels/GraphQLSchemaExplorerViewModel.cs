using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.GraphQL.Schema;

namespace Vegha.App.ViewModels;

/// <summary>One row in the schema explorer list. <see cref="TypeLink"/> names the type the
/// row navigates to when activated (null = inert text row).</summary>
public sealed record SchemaExplorerRow(
    string Title,
    string? Subtitle,
    string? TypeLink,
    bool IsHeader = false,
    bool IsDeprecated = false)
{
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public bool IsNavigable => TypeLink is not null;
}

/// <summary>
/// GraphiQL-style docs explorer over an introspected <see cref="GraphQLSchemaModel"/>:
/// a root page pinning the operation roots + all types, per-type pages with clickable
/// type links (stack navigation), and a debounced substring search across type and
/// field names. Rows are flat records — the view virtualizes them.
/// </summary>
public partial class GraphQLSchemaExplorerViewModel : ObservableObject
{
    private GraphQLSchemaModel? _schema;
    private readonly List<string> _stack = new();
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Breadcrumb text, e.g. "Schema › User".</summary>
    [ObservableProperty]
    private string _breadcrumb = "Schema";

    [ObservableProperty]
    private bool _canGoBack;

    public ObservableCollection<SchemaExplorerRow> Rows { get; } = new();

    /// <summary>Swap in a (re)loaded schema; resets navigation to the root page.</summary>
    public void SetSchema(GraphQLSchemaModel? schema)
    {
        _schema = schema;
        _stack.Clear();
        SearchText = string.Empty;
        RebuildRows();
    }

    [RelayCommand]
    public void NavigateTo(string? typeName)
    {
        if (typeName is null || _schema?.FindType(typeName) is null) return;
        // Ignore self-navigation (clicking the type you're on) to keep Back sensible.
        if (_stack.Count > 0 && _stack[^1] == typeName) return;
        _stack.Add(typeName);
        SearchText = string.Empty;
        RebuildRows();
    }

    [RelayCommand]
    public void Back()
    {
        if (_stack.Count == 0) return;
        _stack.RemoveAt(_stack.Count - 1);
        RebuildRows();
    }

    [RelayCommand]
    public void Home()
    {
        _stack.Clear();
        SearchText = string.Empty;
        RebuildRows();
    }

    partial void OnSearchTextChanged(string value)
    {
        // Debounce: large schemas (60k+ fields) shouldn't rescan per keystroke.
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var uiScheduler = SynchronizationContext.Current is not null
            ? TaskScheduler.FromCurrentSynchronizationContext()
            : TaskScheduler.Current;
        Task.Delay(150, token).ContinueWith(t =>
        {
            if (t.IsCanceled || token.IsCancellationRequested) return;
            RebuildRows();
        }, uiScheduler);
    }

    private void RebuildRows()
    {
        Rows.Clear();
        CanGoBack = _stack.Count > 0;
        Breadcrumb = _stack.Count == 0 ? "Schema" : "Schema › " + string.Join(" › ", _stack);

        if (_schema is null) return;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            BuildSearchRows(_schema, SearchText.Trim());
            return;
        }
        if (_stack.Count == 0) BuildRootRows(_schema);
        else BuildTypeRows(_schema, _stack[^1]);
    }

    private void BuildRootRows(GraphQLSchemaModel schema)
    {
        Rows.Add(new SchemaExplorerRow("ROOTS", null, null, IsHeader: true));
        AddRootRow("query", schema.QueryTypeName);
        AddRootRow("mutation", schema.MutationTypeName);
        AddRootRow("subscription", schema.SubscriptionTypeName);

        Rows.Add(new SchemaExplorerRow("ALL TYPES", null, null, IsHeader: true));
        foreach (var type in schema.Types.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(new SchemaExplorerRow(
                type.Name, KindLabel(type.Kind), type.Name));
        }

        void AddRootRow(string label, string? typeName)
        {
            if (typeName is null) return;
            Rows.Add(new SchemaExplorerRow($"{label}: {typeName}", null, typeName));
        }
    }

    private void BuildTypeRows(GraphQLSchemaModel schema, string typeName)
    {
        var type = schema.FindType(typeName);
        if (type is null) return;

        Rows.Add(new SchemaExplorerRow(type.Name, KindLabel(type.Kind), null, IsHeader: true));
        if (!string.IsNullOrWhiteSpace(type.Description))
            Rows.Add(new SchemaExplorerRow(type.Description!, null, null));

        if (type.Interfaces.Count > 0)
        {
            Rows.Add(new SchemaExplorerRow("IMPLEMENTS", null, null, IsHeader: true));
            foreach (var i in type.Interfaces)
                Rows.Add(new SchemaExplorerRow(i, "interface", i));
        }

        if (type.Fields.Count > 0)
        {
            Rows.Add(new SchemaExplorerRow("FIELDS", null, null, IsHeader: true));
            foreach (var f in type.Fields)
            {
                Rows.Add(new SchemaExplorerRow(
                    FieldSignature(f), f.Description, f.Type.UnwrappedName,
                    IsDeprecated: f.IsDeprecated));
            }
        }

        if (type.InputFields.Count > 0)
        {
            Rows.Add(new SchemaExplorerRow("INPUT FIELDS", null, null, IsHeader: true));
            foreach (var f in type.InputFields)
            {
                var def = f.DefaultValue is null ? string.Empty : $" = {f.DefaultValue}";
                Rows.Add(new SchemaExplorerRow(
                    $"{f.Name}: {f.Type.Display}{def}", f.Description, f.Type.UnwrappedName));
            }
        }

        if (type.EnumValues.Count > 0)
        {
            Rows.Add(new SchemaExplorerRow("VALUES", null, null, IsHeader: true));
            foreach (var v in type.EnumValues)
                Rows.Add(new SchemaExplorerRow(v.Name, v.Description, null, IsDeprecated: v.IsDeprecated));
        }

        if (type.PossibleTypes.Count > 0)
        {
            Rows.Add(new SchemaExplorerRow("POSSIBLE TYPES", null, null, IsHeader: true));
            foreach (var p in type.PossibleTypes)
                Rows.Add(new SchemaExplorerRow(p, null, p));
        }
    }

    private void BuildSearchRows(GraphQLSchemaModel schema, string term)
    {
        const int cap = 200;
        var count = 0;
        Rows.Add(new SchemaExplorerRow($"RESULTS FOR \"{term}\"", null, null, IsHeader: true));
        foreach (var type in schema.Types.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (count >= cap) break;
            if (type.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                Rows.Add(new SchemaExplorerRow(type.Name, KindLabel(type.Kind), type.Name));
                count++;
            }
            foreach (var f in type.Fields)
            {
                if (count >= cap) break;
                if (!f.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                Rows.Add(new SchemaExplorerRow(
                    $"{type.Name}.{FieldSignature(f)}", f.Description, type.Name,
                    IsDeprecated: f.IsDeprecated));
                count++;
            }
        }
        if (count >= cap)
            Rows.Add(new SchemaExplorerRow($"… more than {cap} matches — refine the search", null, null));
        else if (count == 0)
            Rows.Add(new SchemaExplorerRow("No matches", null, null));
    }

    private static string FieldSignature(GraphQLFieldInfo f)
    {
        if (f.Args.Count == 0) return $"{f.Name}: {f.Type.Display}";
        var args = string.Join(", ", f.Args.Select(a => $"{a.Name}: {a.Type.Display}"));
        return $"{f.Name}({args}): {f.Type.Display}";
    }

    private static string KindLabel(GraphQLTypeKind kind) => kind switch
    {
        GraphQLTypeKind.Scalar => "scalar",
        GraphQLTypeKind.Object => "type",
        GraphQLTypeKind.Interface => "interface",
        GraphQLTypeKind.Union => "union",
        GraphQLTypeKind.Enum => "enum",
        GraphQLTypeKind.InputObject => "input",
        _ => "type",
    };
}
