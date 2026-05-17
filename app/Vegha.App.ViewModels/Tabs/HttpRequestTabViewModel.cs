using Vegha.Core.Domain;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>HTTP / GraphQL request tab — wraps a <see cref="RequestEditorViewModel"/> and
/// keeps the tab strip's name/method/dirty mirrors in sync with the editor.</summary>
public class HttpRequestTabViewModel : RequestTabViewModel
{
    public RequestEditorViewModel Editor { get; }

    public override object Workspace => Editor;

    public HttpRequestTabViewModel(RequestEditorViewModel editor, RequestItem? request, string? sourcePath, string id)
    {
        Editor = editor;
        Id = id;
        SourcePath = sourcePath;

        // Initial mirror.
        Method = request?.Method ?? Editor.Method;
        Name = request?.Name ?? "Untitled";
        Kind = request?.Kind ?? RequestKind.Http;
        IsDirty = Editor.IsDirty;

        // Forward editor changes onto the tab fields the strip displays.
        Editor.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(RequestEditorViewModel.Method): Method = Editor.Method; break;
                case nameof(RequestEditorViewModel.IsDirty): IsDirty = Editor.IsDirty; break;
                case nameof(RequestEditorViewModel.Url):
                    // When a draft has no name yet, mirror the URL so the user sees something.
                    if (request is null && !string.IsNullOrEmpty(Editor.Url))
                    {
                        var displayName = ShortenUrl(Editor.Url);
                        if (!string.IsNullOrEmpty(displayName)) Name = displayName;
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
