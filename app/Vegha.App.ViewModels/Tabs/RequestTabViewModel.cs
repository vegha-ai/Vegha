using CommunityToolkit.Mvvm.ComponentModel;
using Vegha.Core.Domain;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>
/// One open request tab. The base type carries the bits the tab strip needs
/// (name, method, dirty dot, source path) and exposes <see cref="Workspace"/>
/// — the inner ViewModel the workspace area binds to. Subclasses pick the
/// per-kind workspace VM (HTTP / WebSocket / gRPC / SOAP / GraphQL).
/// </summary>
public abstract partial class RequestTabViewModel : ObservableObject
{
    /// <summary>Stable handle for de-dup + persistence. Either the request file path on disk
    /// or a generated GUID for unsaved drafts. Settable so a rename (which moves the backing
    /// file) can re-key the tab in place without a close/reopen.</summary>
    public string Id { get; set; } = string.Empty;

    [ObservableProperty]
    private string _name = "Untitled";

    [ObservableProperty]
    private string _method = "GET";

    [ObservableProperty]
    private RequestKind _kind = RequestKind.Http;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Path on disk if the tab is backed by a saved file; null for fresh drafts.
    /// Settable so a rename can repoint the tab at the moved file.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Absolute path of the collection root the tab belongs to. Used by
    /// <see cref="OpenTabsViewModel"/> to filter the tab strip to the active collection.
    /// Null = untagged (legacy persisted tabs, scope-less drafts, or scratch tabs) — these
    /// render in every scope rather than vanishing.</summary>
    public string? CollectionPath { get; set; }

    /// <summary>Workspace this tab belongs to (the workspace folder path). Scratch tabs scope
    /// to their workspace via this; collection tabs scope by <see cref="CollectionPath"/>.</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>True for a quick "+" draft that isn't backed by a collection file. Scratch tabs
    /// live only in the session DB (full state, including any edits), float across the
    /// collections of their workspace, and are discarded on close (or promoted via
    /// "Save to collection…").</summary>
    public bool IsScratch { get; set; }

    /// <summary>True when the tab has unsaved edits AND is backed by a file we can re-read —
    /// i.e. "Revert Changes" can do something. Drafts with no source can't be reverted.</summary>
    public bool CanRevert => IsDirty && !string.IsNullOrEmpty(SourcePath);

    /// <summary>The per-kind ViewModel that the workspace area's ContentControl binds to.</summary>
    public abstract object Workspace { get; }
}
