using Vegha.Core.Domain;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>
/// Workspace tab hosting a folder's settings — Overview (location, request count, docs) plus
/// the Headers / Vars / Auth / Script / Tests editors that cascade to every request inside it.
/// Replaces the old modal Properties dialog so a single click on a folder row lands here.
/// Save writes <c>folder.bru</c> through <see cref="CollectionsViewModel.ApplyFolderSettings"/>
/// and rehydrates from the reloaded folder.
/// </summary>
public sealed partial class FolderSettingsTabViewModel : NodeSettingsTabViewModel
{
    private readonly CollectionsViewModel _collections;

    /// <summary>The folder's directory — stable across reloads, so the tab keys and
    /// re-resolves by it.</summary>
    public string FolderPath { get; }

    public override string NodePath => FolderPath;
    public override bool IsCollection => false;

    public static string BuildId(string folderPath) => "foldersettings:" + folderPath;

    public FolderSettingsTabViewModel(CollectionsViewModel collections, CollectionFolderViewModel folder, string? collectionPath)
    {
        _collections = collections;
        FolderPath = folder.Path;
        Id = BuildId(folder.Path);
        // Scope to the owning collection so the strip keeps it with that collection's tabs.
        CollectionPath = collectionPath;
        Method = "DIR";
        Kind = RequestKind.Http;
        Hydrate(folder);
    }

    private void Hydrate(CollectionFolderViewModel folder)
    {
        Name = folder.Name + " — Settings";
        Location = folder.Path;
        var model = folder.Folder ?? new Folder { Name = folder.Name };
        SetProps(new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Folder, model));
        // Linked-request picker offers every request in the owning collection.
        if (_collections.FindRootForDirectory(folder.Path)?.Collection is { } owner)
            Props.LinkableRequestPaths = NodePropertiesViewModel.LinkablePathsOf(owner);
        RequestCount = folder.Folder is null ? 0 : CollectionsViewModel.CountRequestsInFolderPublic(folder.Folder);
    }

    protected override void Save(NodeSnapshot snapshot)
    {
        var reloaded = _collections.ApplyFolderSettings(FolderPath, snapshot);
        if (reloaded is not null) Hydrate(reloaded);
    }
}
