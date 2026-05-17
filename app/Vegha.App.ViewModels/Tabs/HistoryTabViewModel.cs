namespace Vegha.App.ViewModels.Tabs;

/// <summary>Tab that shows a historical request + response. Inherits the HTTP request tab so
/// the regular <c>RequestWorkspace</c> + <c>ResponseDisplay</c> views render it unchanged —
/// this subclass exists purely as a type marker for the activity-rail mode filter (history
/// tabs only show while the History sidebar section is active, mirroring how
/// <see cref="GitDiffTabViewModel"/> only shows in Git mode).
///
/// Tab Id convention: <c>"history:" + HistoryId</c>. Doubles as the dedupe key for
/// <see cref="OpenTabsViewModel.OpenHistoryTab"/> and as the marker the restore path uses to
/// re-resolve the entry from <c>HistoryStore</c> on startup.</summary>
public sealed class HistoryTabViewModel : HttpRequestTabViewModel
{
    /// <summary>SQLite row id of the source history entry.</summary>
    public long HistoryId { get; }

    public HistoryTabViewModel(RequestEditorViewModel editor, long historyId)
        : base(editor, request: null, sourcePath: null, id: "history:" + historyId)
    {
        HistoryId = historyId;
    }

    public static string BuildId(long historyId) => "history:" + historyId;

    /// <summary>Tries to parse the history row id back out of a tab id. Returns false for any
    /// id that isn't a history tab.</summary>
    public static bool TryParseId(string id, out long historyId)
    {
        if (id is not null && id.StartsWith("history:", StringComparison.Ordinal)
            && long.TryParse(id.AsSpan("history:".Length), out historyId))
        {
            return true;
        }
        historyId = 0;
        return false;
    }
}
