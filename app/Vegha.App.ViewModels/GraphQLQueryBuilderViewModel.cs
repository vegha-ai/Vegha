using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vegha.Core.GraphQL;
using Vegha.Core.GraphQL.Builder;
using Vegha.Core.GraphQL.Schema;

namespace Vegha.App.ViewModels;

/// <summary>An argument row under a checked field: type-aware inline value input.
/// The value is inserted into the query verbatim, except String-family scalars which are
/// auto-quoted unless the input already looks like a literal/variable/interpolation.</summary>
public partial class BuilderArgViewModel : ObservableObject
{
    private readonly GraphQLQueryBuilderViewModel _owner;
    internal readonly GraphQLArgInfo Info;

    public BuilderArgViewModel(GraphQLQueryBuilderViewModel owner, GraphQLArgInfo info)
    {
        _owner = owner;
        Info = info;
    }

    public string Name => Info.Name;
    public string TypeDisplay => Info.Type.Display;
    public string? Description => Info.Description;
    public bool IsRequired => Info.Type.Kind == TypeRefKind.NonNull && Info.DefaultValue is null;

    /// <summary>Raw value text; empty = argument omitted from the query.</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    // Uniform row surface so the TreeViewItem style bindings resolve on every row type.
    public bool IsVisible => true;
    public bool IsExpanded { get => false; set { } }

    partial void OnValueChanged(string value) => _owner.OnTreeEdited();

    /// <summary>The GraphQL literal that goes on the wire for this input.</summary>
    internal string? ToLiteral()
    {
        var v = Value.Trim();
        if (v.Length == 0) return null;
        // Verbatim passthrough for anything already literal-shaped.
        if (v.StartsWith('"') || v.StartsWith('$') || v.StartsWith('{') || v.StartsWith('[')
            || v.StartsWith("{{", StringComparison.Ordinal))
            return v;
        var typeName = Info.Type.UnwrappedName;
        if (typeName == "String")
            return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        if (typeName == "ID" && !v.All(char.IsAsciiDigit))
            return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return v; // Int/Float/Boolean/enums/custom scalars: verbatim
    }
}

/// <summary>One field row in the builder tree. Children (args first, then subfields) are
/// materialized lazily on first expansion — schemas are cyclic, so eager recursion never ends.</summary>
public partial class BuilderFieldViewModel : ObservableObject
{
    private readonly GraphQLQueryBuilderViewModel _owner;
    internal readonly GraphQLFieldInfo Info;
    internal readonly BuilderFieldViewModel? Parent;
    private bool _childrenMaterialized;

    public BuilderFieldViewModel(
        GraphQLQueryBuilderViewModel owner, GraphQLFieldInfo info, BuilderFieldViewModel? parent)
    {
        _owner = owner;
        Info = info;
        Parent = parent;
        IsComposite = owner.IsComposite(info.Type.UnwrappedName);
        // Argument input rows render as the field's first children (Postman layout).
        foreach (var a in info.Args)
        {
            var arg = new BuilderArgViewModel(owner, a);
            Args.Add(arg);
            Children.Add(arg);
        }
        // Lazy-expansion placeholder so the TreeView shows an expander chevron.
        if (IsComposite) Children.Add(LazyPlaceholder.Instance);
    }

    public string Name => Info.Name;
    public string TypeDisplay => Info.Type.Display;
    public string? Description => Info.Description;
    public bool IsDeprecated => Info.IsDeprecated;

    /// <summary>Object/interface return type — needs a selection set.</summary>
    public bool IsComposite { get; }

    public ObservableCollection<BuilderArgViewModel> Args { get; } = new();

    /// <summary>Display children: argument rows first, then subfields (a placeholder until
    /// first expansion materializes them).</summary>
    public ObservableCollection<object> Children { get; } = new();

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isVisible = true;

    partial void OnIsCheckedChanged(bool value)
    {
        if (_owner.Suppressed) return;
        if (value)
        {
            // A selection is only reachable when every ancestor is selected.
            for (var p = Parent; p is not null; p = p.Parent) p.IsChecked = true;
            if (IsComposite) IsExpanded = true;
        }
        else
        {
            foreach (var child in MaterializedFields()) child.IsChecked = false;
        }
        _owner.OnTreeEdited();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) MaterializeChildren();
    }

    public IEnumerable<BuilderFieldViewModel> MaterializedFields() =>
        Children.OfType<BuilderFieldViewModel>();

    public void MaterializeChildren()
    {
        if (_childrenMaterialized || !IsComposite) return;
        _childrenMaterialized = true;
        Children.Remove(LazyPlaceholder.Instance);
        foreach (var f in _owner.FieldsOf(Info.Type.UnwrappedName))
            Children.Add(new BuilderFieldViewModel(_owner, f, this));
    }
}

/// <summary>Marker item rendered as an empty row; replaced by real children on expansion.</summary>
public sealed class LazyPlaceholder
{
    public static readonly LazyPlaceholder Instance = new();
    private LazyPlaceholder() { }

    public bool IsVisible => true;
    public bool IsExpanded { get => false; set { } }
}

/// <summary>A root group ("Query" / "Mutation" / "Subscription") in the builder tree.</summary>
public partial class BuilderRootViewModel : ObservableObject
{
    public BuilderRootViewModel(GraphQLOperationKind kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public GraphQLOperationKind Kind { get; }
    public string Label { get; }
    public ObservableCollection<object> Children { get; } = new();

    public bool IsVisible => true;

    [ObservableProperty]
    private bool _isExpanded;

    public string? OperationName { get; set; }

    public IEnumerable<BuilderFieldViewModel> Fields() => Children.OfType<BuilderFieldViewModel>();
}

/// <summary>
/// Postman-style GraphQL query builder: a checkbox tree over the introspected schema that
/// composes the query document. Checking fields (and filling argument boxes) regenerates the
/// text; hand-edits to the text re-sync the tree (debounced by the editor VM). Documents
/// using fragments/aliases/directives flip the builder read-only rather than risk a lossy
/// regenerate.
/// </summary>
public partial class GraphQLQueryBuilderViewModel : ObservableObject
{
    private GraphQLSchemaModel? _schema;
    private string _lastGeneratedQuery = string.Empty;

    /// <summary>Raised with the regenerated query text after any tree edit.</summary>
    public event Action<string>? QueryRegenerated;

    public ObservableCollection<BuilderRootViewModel> Roots { get; } = new();

    /// <summary>True while programmatic updates run (schema load / text→tree sync) so
    /// checkbox handlers don't echo back into the text.</summary>
    internal bool Suppressed { get; private set; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Read-only reason (fragments/aliases/directives in the document); null = editable.</summary>
    [ObservableProperty]
    private string? _readOnlyReason;

    public bool HasSchema => _schema is not null;

    // ---- schema plumbing (used by node VMs) ----

    internal bool IsComposite(string? typeName) =>
        _schema?.FindType(typeName)?.Kind
            is GraphQLTypeKind.Object or GraphQLTypeKind.Interface;

    internal IReadOnlyList<GraphQLFieldInfo> FieldsOf(string? typeName) =>
        _schema?.FindType(typeName)?.Fields ?? Array.Empty<GraphQLFieldInfo>();

    public void SetSchema(GraphQLSchemaModel? schema, string currentQuery)
    {
        _schema = schema;
        Suppressed = true;
        try
        {
            Roots.Clear();
            if (schema is null) return;
            AddRoot(GraphQLOperationKind.Query, schema.QueryTypeName);
            AddRoot(GraphQLOperationKind.Mutation, schema.MutationTypeName);
            AddRoot(GraphQLOperationKind.Subscription, schema.SubscriptionTypeName);
            if (Roots.Count > 0) Roots[0].IsExpanded = true;
        }
        finally
        {
            Suppressed = false;
            OnPropertyChanged(nameof(HasSchema));
        }
        SyncFromQuery(currentQuery);

        void AddRoot(GraphQLOperationKind kind, string? rootTypeName)
        {
            var type = schema!.FindType(rootTypeName);
            if (type is null) return;
            var root = new BuilderRootViewModel(kind, kind.ToString());
            foreach (var f in type.Fields)
                root.Children.Add(new BuilderFieldViewModel(this, f, parent: null));
            Roots.Add(root);
        }
    }

    // ---- text → tree ----

    /// <summary>Reflects the query text into checkbox/argument state. Skips no-ops (text the
    /// builder itself just generated) and transient syntax errors; flips read-only when the
    /// document uses constructs the tree can't represent.</summary>
    public void SyncFromQuery(string? query)
    {
        if (_schema is null) return;
        if (string.Equals(query ?? string.Empty, _lastGeneratedQuery, StringComparison.Ordinal)) return;

        var parsed = GraphQLSelectionDocument.Parse(query);
        if (!parsed.IsBuilderCompatible)
        {
            // Syntax errors are transient states while typing — keep the current tree live.
            if (parsed.IncompatibleReason == "syntax error") return;
            ReadOnlyReason = parsed.IncompatibleReason;
            return;
        }
        ReadOnlyReason = null;

        Suppressed = true;
        try
        {
            foreach (var root in Roots)
            {
                var op = parsed.Operations.FirstOrDefault(o => o.Kind == root.Kind);
                root.OperationName = op?.Name;
                ApplySelections(root.Fields(), op?.Selections ?? new List<SelectionNode>());
                if (op is { Selections.Count: > 0 }) root.IsExpanded = true;
            }
        }
        finally
        {
            Suppressed = false;
        }
    }

    private void ApplySelections(IEnumerable<BuilderFieldViewModel> fields, List<SelectionNode> selections)
    {
        foreach (var field in fields)
        {
            var selected = selections.FirstOrDefault(s => s.Name == field.Name);
            field.IsChecked = selected is not null;
            foreach (var arg in field.Args)
            {
                arg.Value = selected?.Args
                    .FirstOrDefault(a => a.Key == arg.Name).Value ?? string.Empty;
            }
            if (selected is not null && selected.Children.Count > 0)
            {
                field.MaterializeChildren();
                field.IsExpanded = true;
                ApplySelections(field.MaterializedFields(), selected.Children);
            }
            else if (field.IsComposite)
            {
                // Unchecked (or leaf-selected) subtree: clear any previously-checked descendants.
                foreach (var child in field.MaterializedFields())
                    ClearSubtree(child);
            }
        }

        static void ClearSubtree(BuilderFieldViewModel field)
        {
            field.IsChecked = false;
            foreach (var arg in field.Args) arg.Value = string.Empty;
            foreach (var child in field.MaterializedFields()) ClearSubtree(child);
        }
    }

    // ---- tree → text ----

    internal void OnTreeEdited()
    {
        if (Suppressed || ReadOnlyReason is not null) return;
        var operations = new List<SelectionOperation>();
        foreach (var root in Roots)
        {
            var op = new SelectionOperation { Kind = root.Kind, Name = root.OperationName };
            foreach (var field in root.Fields())
            {
                if (BuildNode(field) is { } node) op.Selections.Add(node);
            }
            operations.Add(op);
        }
        _lastGeneratedQuery = GraphQLSelectionDocument.Render(operations);
        QueryRegenerated?.Invoke(_lastGeneratedQuery);
    }

    private SelectionNode? BuildNode(BuilderFieldViewModel field)
    {
        if (!field.IsChecked) return null;
        var node = new SelectionNode { Name = field.Name };
        foreach (var arg in field.Args)
        {
            if (arg.ToLiteral() is { } literal)
                node.Args.Add(new(arg.Name, literal));
        }
        foreach (var child in field.MaterializedFields())
        {
            if (BuildNode(child) is { } childNode) node.Children.Add(childNode);
        }
        // Composite field with nothing picked yet: emit `{ }` so the text shows where
        // selections are still required (matches Postman; the squiggle is the prompt).
        node.ForceSelectionSet = field.IsComposite && node.Children.Count == 0;
        return node;
    }

    // ---- search ----

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Name filter over materialized rows: a row stays visible when it or any
    /// materialized descendant matches. (Unexpanded subtrees aren't searched — expansion
    /// is lazy by design; expand a branch to include it.)</summary>
    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        foreach (var root in Roots)
            foreach (var field in root.Fields())
                FilterNode(field, term);
    }

    private static bool FilterNode(BuilderFieldViewModel field, string term)
    {
        var selfMatch = term.Length == 0
            || field.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
        var childMatch = false;
        foreach (var child in field.MaterializedFields())
            childMatch |= FilterNode(child, term);
        field.IsVisible = selfMatch || childMatch;
        return field.IsVisible;
    }
}
