using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Domain;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>
/// Workspace tab hosting a collection's or a folder's settings — Overview (location, request
/// count, docs) plus the Headers / Vars / Auth / Script / Tests editors, and Presets for
/// collections. One tab type serves both scopes because the inheritance surface is identical;
/// only the Overview stats and the Presets tab differ.
///
/// The editing surface is a <see cref="NodePropertiesViewModel"/>, which in turn hosts the
/// shared <see cref="AuthSectionViewModel"/> — the very same auth editor the request tab uses.
/// Save re-resolves the node by path (a reload swaps the tree VMs) and rehydrates from the
/// reloaded state.
/// </summary>
public sealed partial class NodeSettingsTabViewModel : RequestTabViewModel
{
    private readonly CollectionsViewModel _collections;

    /// <summary>Collection or folder. Named to avoid colliding with the base tab's
    /// <see cref="RequestTabViewModel.Kind"/> (which is the request kind).</summary>
    public NodePropertiesViewModel.Kind NodeKind { get; }

    /// <summary>The node's on-disk directory — stable across reloads (which swap the tree VM
    /// instances), so we key + re-resolve by it rather than a captured reference.</summary>
    public string NodePath { get; }

    public bool IsCollection => NodeKind == NodePropertiesViewModel.Kind.Collection;

    // ---- Scope-aware copy. The editors are identical at both levels; only the wording
    // that names the scope changes. ----

    public string RequestScopeLabel => IsCollection ? " request(s) in collection" : " request(s) in folder";

    public string DocsEmptyHint => IsCollection
        ? "Document this collection's purpose, endpoints, and conventions. Markdown is supported — headings, lists, code blocks, tables, and links."
        : "Document what this folder groups and any conventions its requests follow. Markdown is supported — headings, lists, code blocks, tables, and links.";

    public string PreRequestHint => IsCollection
        ? "Pre-request — runs before every request in this collection."
        : "Pre-request — runs before every request in this folder, after the collection's.";

    public string DocsWatermark => IsCollection
        ? "# Document this collection (Markdown)"
        : "# Document this folder (Markdown)";

    public string TestsHint => IsCollection
        ? "Runs after every request in this collection. Use test('name', fn) and expect(actual)."
        : "Runs after every request in this folder. Use test('name', fn) and expect(actual).";

    // Empty-state subtitles for the script panes. One sentence saying what appears here and
    // how to make it appear — the designed empty state, not a watermark in the editor.

    public string PreRequestEmptyHint => IsCollection
        ? "Start typing to run JavaScript before every request in this collection."
        : "Start typing to run JavaScript before every request in this folder.";

    public string PostResponseEmptyHint => IsCollection
        ? "Start typing to run JavaScript after every response in this collection."
        : "Start typing to run JavaScript after every response in this folder.";

    public string TestsEmptyHint => IsCollection
        ? "Start typing to assert against every response in this collection — test('status is 200', () => expect(res.getStatus()).to.equal(200))."
        : "Start typing to assert against every response in this folder — test('status is 200', () => expect(res.getStatus()).to.equal(200)).";

    /// <summary>The editing surface. Reassigned on rehydrate after a save, so it raises
    /// PropertyChanged for the bound content to re-bind.</summary>
    [ObservableProperty]
    private NodePropertiesViewModel _props = null!;

    // ---- Overview stats ----
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private int _requestCount;
    [ObservableProperty] private int _collectionEnvCount;
    [ObservableProperty] private int _globalEnvCount;

    /// <summary>Documentation edit/view toggle. The Overview shows EITHER the Markdown editor
    /// (when true) OR the rendered preview / empty-state placeholder (when false) — never both.
    /// Resets to view mode on (re)hydrate.</summary>
    [ObservableProperty] private bool _isDocsEditing;

    /// <summary>Flips between the docs editor and the rendered preview.</summary>
    [RelayCommand]
    private void ToggleDocsEdit() => IsDocsEditing = !IsDocsEditing;

    public override object Workspace => this;

    public static string BuildId(NodePropertiesViewModel.Kind kind, string path) =>
        (kind == NodePropertiesViewModel.Kind.Collection ? "colsettings:" : "foldersettings:") + path;

    public NodeSettingsTabViewModel(CollectionsViewModel collections, CollectionRootViewModel root)
    {
        _collections = collections;
        NodeKind = NodePropertiesViewModel.Kind.Collection;
        NodePath = root.SourcePath;
        Id = BuildId(NodeKind, NodePath);
        // Scope the tab to its collection so the strip filtering keeps it with the collection
        // it belongs to (same mechanism request tabs use).
        CollectionPath = root.SourcePath;
        Method = "CFG";
        Kind = RequestKind.Http;
        HydrateCollection(root);
    }

    public NodeSettingsTabViewModel(CollectionsViewModel collections, CollectionFolderViewModel folder)
    {
        _collections = collections;
        NodeKind = NodePropertiesViewModel.Kind.Folder;
        NodePath = folder.Path;
        Id = BuildId(NodeKind, NodePath);
        CollectionPath = collections.FindRootForDirectory(folder.Path)?.SourcePath;
        Method = "CFG";
        Kind = RequestKind.Http;
        HydrateFolder(folder);
    }

    private void HydrateCollection(CollectionRootViewModel root)
    {
        Name = root.Name + " — Settings";
        Location = root.SourcePath;

        var col = root.Collection ?? new Collection { Name = root.Name };
        AttachProps(new NodePropertiesViewModel(
            NodePropertiesViewModel.Kind.Collection, col, _collections.OAuth2Acquirer));

        RequestCount = root.Collection is null ? 0 : CollectionsViewModel.CountRequestsPublic(root.Collection);
        CollectionEnvCount = root.Collection?.Environments.Count ?? 0;
        GlobalEnvCount = _collections.GlobalEnvironments.Count;

        // Land in view mode after (re)hydrate — a fresh save returns you to the rendered docs.
        IsDocsEditing = false;
    }

    private void HydrateFolder(CollectionFolderViewModel folder)
    {
        Name = folder.Name + " — Settings";
        Location = folder.Path;

        var model = folder.Folder ?? new Folder { Name = folder.Name };
        AttachProps(new NodePropertiesViewModel(
            NodePropertiesViewModel.Kind.Folder, model, _collections.OAuth2Acquirer));

        WireFolderInheritance(folder);

        RequestCount = folder.Folder is null ? 0 : CollectionsViewModel.CountRequestsInFolderPublic(folder.Folder);
        // Environments are a collection-level concept; the folder Overview doesn't show them.
        CollectionEnvCount = 0;
        GlobalEnvCount = 0;

        IsDocsEditing = false;
    }

    /// <summary>Gives the folder's auth panel the same "Inherited from …" banner + Override
    /// action a request gets, resolved against its ancestor folders and the collection.</summary>
    private void WireFolderInheritance(CollectionFolderViewModel folder)
    {
        var (collection, outerChain) = _collections.ResolveOuterFolderChain(folder);
        if (collection is null) return;

        // A probe request that declares nothing of its own, so composition reports purely what
        // the ancestors contribute.
        var probe = new RequestItem { Name = folder.Name };
        var auth = Props.Auth;

        auth.InheritedAuthProvider = () => Vegha.Core.Requests.RequestComposition
            .Compose(collection, outerChain, probe,
                Vegha.Core.Requests.RequestComposition.WorkspaceContext.Empty).Auth;

        void Refresh()
        {
            // The banner only makes sense while this folder isn't defining auth itself.
            if (auth.HasData) { auth.InheritedFrom = null; return; }
            var (_, sources) = Vegha.Core.Requests.RequestComposition.ComposeWithSources(
                collection, outerChain, probe,
                Vegha.Core.Requests.RequestComposition.WorkspaceContext.Empty);
            auth.InheritedFrom = sources.Auth;
        }

        auth.Changed += (_, _) => Refresh();
        auth.OverrideApplied += (_, _) => Refresh();
        Refresh();
    }

    private void AttachProps(NodePropertiesViewModel props)
    {
        if (Props is not null) Props.SaveRequested -= OnPropsSaved;
        props.SaveRequested += OnPropsSaved;
        Props = props;
    }

    private void OnPropsSaved(object? sender, NodePropertiesSaveEventArgs e)
    {
        // Rehydrate from the reloaded (swapped) node so a second save doesn't build on stale
        // state, and the Overview counts refresh.
        if (NodeKind == NodePropertiesViewModel.Kind.Collection)
        {
            var reloaded = _collections.ApplyCollectionSettings(NodePath, e.Snapshot);
            if (reloaded is not null) HydrateCollection(reloaded);
        }
        else
        {
            var reloaded = _collections.ApplyFolderSettings(NodePath, e.Snapshot);
            if (reloaded is not null) HydrateFolder(reloaded);
        }
        IsDirty = false;
    }
}
