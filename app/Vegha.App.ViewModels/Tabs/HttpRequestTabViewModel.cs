using Vegha.Core.Domain;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>HTTP / GraphQL request tab — wraps a <see cref="RequestEditorViewModel"/> and
/// keeps the tab strip's name/method/dirty mirrors in sync with the editor.</summary>
public class HttpRequestTabViewModel : RequestTabViewModel
{
    public RequestEditorViewModel Editor { get; }

    public override object Workspace => Editor;

    /// <summary>Delegates to the editor, which owns the name. Reading and writing the same cell the
    /// save path emits from is what makes tab-label / .bru drift structurally impossible — there is
    /// no second copy left to fall out of step.</summary>
    public override string Name
    {
        get => Editor.RequestName;
        set => Editor.RequestName = value;
    }

    public HttpRequestTabViewModel(RequestEditorViewModel editor, RequestItem? request, string? sourcePath, string id)
    {
        Editor = editor;
        Id = id;
        SourcePath = sourcePath;

        // Initial mirror. Name is NOT mirrored — it's delegated to the editor above. Seed the
        // editor's cell only when it has nothing yet, so constructing a tab around an already
        // loaded editor can't blank out the name it was loaded with.
        Method = request?.Method ?? Editor.Method;
        if (string.IsNullOrEmpty(Editor.RequestName))
            Editor.RequestName = request?.Name ?? "Untitled";
        Kind = Editor.BodyType == "graphql" ? RequestKind.GraphQL : request?.Kind ?? RequestKind.Http;
        IsDirty = Editor.IsDirty;

        // Forward editor changes onto the tab fields the strip displays.
        Editor.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(RequestEditorViewModel.Method): Method = Editor.Method; break;
                case nameof(RequestEditorViewModel.IsDirty): IsDirty = Editor.IsDirty; break;
                // The name cell lives in the editor; re-raise so the tab strip repaints.
                case nameof(RequestEditorViewModel.RequestName): OnPropertyChanged(nameof(Name)); break;
                case nameof(RequestEditorViewModel.BodyType):
                    // Keep the tab's GraphQL badge in sync when the body type flips. Only
                    // toggle between Http/GraphQL — WS/gRPC drafts also ride this VM and
                    // their Kind must not be clobbered by a body-type edit.
                    if (Kind is RequestKind.Http or RequestKind.GraphQL)
                        Kind = Editor.BodyType == "graphql" ? RequestKind.GraphQL : RequestKind.Http;
                    break;
                case nameof(RequestEditorViewModel.Url):
                    // A draft with no name yet mirrors the URL so the tab shows something useful.
                    // HasExplicitName is an extra guard on top of the original construction-time
                    // check: `request is null` only describes how the tab started out, so a draft
                    // named later kept having its label rewritten out from under the user.
                    if (request is null && !Editor.HasExplicitName && !string.IsNullOrEmpty(Editor.Url))
                    {
                        var displayName = ShortenUrl(Editor.Url);
                        if (!string.IsNullOrEmpty(displayName)) Editor.RequestName = displayName;
                    }
                    break;
            }
        };
    }

    /// <summary>Forwards the parent (collection + folder chain) to the editor so SendAsync
    /// can compose inherited headers / auth / vars / scripts at execution time.</summary>
    public void SetParentContext(
        Vegha.Core.Domain.Collection? collection,
        IReadOnlyList<Vegha.Core.Domain.Folder>? folderChain)
    {
        Editor.SetParentContext(collection, folderChain);
    }

    private static string ShortenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
                ? uri.Host
                : uri.Host + uri.AbsolutePath;
        return url.Length > 32 ? url[..32] + "…" : url;
    }
}
