using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Detects drift between an OpenAPI spec on disk and the operations the user has imported
/// into a collection. The detector matches on (method, normalized path) — operations the
/// spec adds, removes, or whose path/method changes show up as <see cref="DriftEntry"/>
/// rows the UI can render.
/// </summary>
public static class OpenApiDriftDetector
{
    public enum DriftKind
    {
        AddedInSpec,
        RemovedFromSpec,
        Unchanged,
    }

    public sealed record DriftEntry(DriftKind Kind, string Method, string Path, string? RequestName);

    /// <summary>Compares the requests projected from <paramref name="liveSpec"/> against
    /// the requests already in <paramref name="userCollection"/>. Returns one entry per
    /// operation, with the drift kind set.</summary>
    public static IReadOnlyList<DriftEntry> Compare(Collection userCollection, Collection liveSpec)
    {
        var live = FlattenOperations(liveSpec).ToDictionary(o => $"{o.Method}|{o.Path}", o => o);
        var user = FlattenOperations(userCollection).ToDictionary(o => $"{o.Method}|{o.Path}", o => o);

        var entries = new List<DriftEntry>();
        // Anything new on the spec side that the user doesn't have.
        foreach (var (key, op) in live)
        {
            if (!user.ContainsKey(key))
                entries.Add(new DriftEntry(DriftKind.AddedInSpec, op.Method, op.Path, op.Name));
        }
        // Anything the user has that the spec dropped.
        foreach (var (key, op) in user)
        {
            if (!live.ContainsKey(key))
                entries.Add(new DriftEntry(DriftKind.RemovedFromSpec, op.Method, op.Path, op.Name));
        }
        // Unchanged — useful for the UI to render the full state, not just deltas.
        foreach (var (key, op) in user)
        {
            if (live.ContainsKey(key))
                entries.Add(new DriftEntry(DriftKind.Unchanged, op.Method, op.Path, op.Name));
        }

        return entries
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Method, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private record FlatOperation(string Method, string Path, string Name);

    private static IEnumerable<FlatOperation> FlattenOperations(Collection collection)
    {
        foreach (var r in collection.Requests)
            yield return new FlatOperation(r.Method, NormalizePath(r.Url), r.Name);

        foreach (var folder in collection.Folders)
            foreach (var op in FlattenFolder(folder))
                yield return op;
    }

    private static IEnumerable<FlatOperation> FlattenFolder(Folder folder)
    {
        foreach (var r in folder.Requests)
            yield return new FlatOperation(r.Method, NormalizePath(r.Url), r.Name);
        foreach (var sub in folder.Folders)
            foreach (var op in FlattenFolder(sub))
                yield return op;
    }

    /// <summary>Strips the {{baseUrl}} prefix and any trailing slash so /users and
    /// {{baseUrl}}/users compare equal.</summary>
    private static string NormalizePath(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        var path = url;
        const string baseUrlMarker = "{{baseUrl}}";
        var idx = path.IndexOf(baseUrlMarker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) path = path[(idx + baseUrlMarker.Length)..];
        // Drop leading scheme://host if the URL was absolute without baseUrl.
        // Done manually (not via Uri.TryCreate) because on Unix the Uri class
        // happily parses "/foo" as an absolute file:// URI and then percent-
        // encodes path segments like "{id}" → "%7Bid%7D", which silently
        // changes the OpenAPI parameter syntax we're trying to compare.
        var schemeSep = path.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep > 0)
        {
            var hostSlash = path.IndexOf('/', schemeSep + 3);
            path = hostSlash > 0 ? path[hostSlash..] : "/";
        }
        return path.TrimEnd('/');
    }
}
