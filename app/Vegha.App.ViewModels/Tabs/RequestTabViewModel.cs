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
    /// or a generated GUID for unsaved drafts.</summary>
    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    private string _name = "Untitled";

    [ObservableProperty]
    private string _method = "GET";

    [ObservableProperty]
    private RequestKind _kind = RequestKind.Http;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Path on disk if the tab is backed by a saved file; null for fresh drafts.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Absolute path of the collection root the tab belongs to. Used by
    /// <see cref="OpenTabsViewModel"/> to filter the tab strip to the active collection.
    /// Null = untagged (legacy persisted tabs or scope-less drafts) — these render in
    /// every scope rather than vanishing.</summary>
    public string? CollectionPath { get; init; }

    /// <summary>The per-kind ViewModel that the workspace area's ContentControl binds to.</summary>
    public abstract object Workspace { get; }
}
