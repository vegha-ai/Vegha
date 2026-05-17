using Vegha.Core.Domain;

namespace Vegha.Core.Requests;

/// <summary>
/// Resolves the effective request that goes on the wire by walking the
/// Collection → Folder chain → Request and merging headers / auth / vars / scripts
/// according to Bruno's inheritance rules.
///
/// Headers: outer layers concat then later layers override same-key entries.
/// Auth: nearest non-Inherit wins (request → innermost folder → outermost folder → collection).
/// Vars: union, last-wins (collection → folders innermost-last → request).
/// Pre-request, post-response, tests scripts: concatenated top-down
/// (collection first, then folders outer→inner, then request).
/// Docs: not inherited; request value used as-is.
/// </summary>
public static class RequestComposition
{
    /// <summary>Workspace-level inheritance payload — variables and pre/post/test scripts that
    /// merge underneath the collection layer. Read from <c>&lt;workspace&gt;/environments/</c>
    /// and <c>&lt;workspace&gt;/scripts/</c> by <c>WorkspaceModelLoader</c>; null treated as empty.</summary>
    public sealed record WorkspaceContext(
        IReadOnlyList<KvPair>? Variables,
        string? PreRequestScript,
        string? PostResponseScript,
        string? TestsScript)
    {
        public static WorkspaceContext Empty { get; } = new(null, null, null, null);
    }

    /// <summary>The composed view a request executor (or codegen) should treat as authoritative.</summary>
    public sealed record Composed(
        IReadOnlyList<KvPair> Headers,
        AuthConfig? Auth,
        IReadOnlyDictionary<string, string> Vars,
        string? PreRequestScript,
        string? PostResponseScript,
        string? TestsScript,
        string? Docs);

    /// <summary>Where each inheritable value originated, so the editor can render
    /// "Inherited from &lt;layer&gt;" hints + Override buttons. Null on a field means
    /// the request itself owns it (no inheritance happening).</summary>
    public sealed record Sources(
        string? Auth,
        string? PreRequestScript,
        string? PostResponseScript,
        string? TestsScript,
        IReadOnlyDictionary<string, string> Headers);

    /// <summary>Diagnostic variant of <see cref="Compose"/> that also returns the
    /// per-field origin of every inherited value.</summary>
    public static (Composed Composed, Sources Sources) ComposeWithSources(
        Collection collection,
        IReadOnlyList<Folder> folderChain,
        RequestItem request)
        => ComposeWithSources(collection, folderChain, request, WorkspaceContext.Empty);

    /// <summary>Compose with an explicit workspace layer underneath the collection.
    /// Workspace vars/scripts are emitted into <see cref="Composed"/> using the existing
    /// last-wins / top-down rules.</summary>
    public static (Composed Composed, Sources Sources) ComposeWithSources(
        Collection collection,
        IReadOnlyList<Folder> folderChain,
        RequestItem request,
        WorkspaceContext workspace)
    {
        var composed = Compose(collection, folderChain, request, workspace);

        var authSource = ResolveAuthSource(collection, folderChain, request);
        var preScriptSource = ResolveScriptSource(
            workspace.PreRequestScript,
            collection.PreRequestScript,
            folderChain.Select((f, i) => (i, f.Name, f.PreRequestScript)).ToList(),
            request.PreRequestScript,
            collection.Name);
        var postScriptSource = ResolveScriptSource(
            workspace.PostResponseScript,
            collection.PostResponseScript,
            folderChain.Select((f, i) => (i, f.Name, f.PostResponseScript)).ToList(),
            request.PostResponseScript,
            collection.Name);
        var testsSource = ResolveScriptSource(
            workspace.TestsScript,
            collection.TestsScript,
            folderChain.Select((f, i) => (i, f.Name, f.TestsScript)).ToList(),
            request.Tests,
            collection.Name);
        var headerSources = ResolveHeaderSources(collection, folderChain, request);

        return (composed, new Sources(authSource, preScriptSource, postScriptSource, testsSource, headerSources));
    }

    private static string? ResolveAuthSource(
        Collection collection, IReadOnlyList<Folder> folderChain, RequestItem request)
    {
        if (HasConcreteAuth(request.Auth)) return null;          // request owns it
        for (var i = folderChain.Count - 1; i >= 0; i--)
            if (HasConcreteAuth(folderChain[i].Auth))
                return $"folder “{folderChain[i].Name}”";
        if (HasConcreteAuth(collection.Auth)) return $"collection “{collection.Name}”";
        return null;
    }

    private static string? ResolveScriptSource(
        string? workspaceScript,
        string? collectionScript,
        IReadOnlyList<(int Index, string Name, string? Script)> folderScripts,
        string? requestScript,
        string collectionName)
    {
        // For scripts we concatenate, so "inherited" only makes sense when the request
        // itself has nothing — otherwise the request owns it (with possible parents prepended).
        if (!string.IsNullOrWhiteSpace(requestScript)) return null;
        for (var i = folderScripts.Count - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(folderScripts[i].Script))
                return $"folder “{folderScripts[i].Name}”";
        if (!string.IsNullOrWhiteSpace(collectionScript)) return $"collection “{collectionName}”";
        if (!string.IsNullOrWhiteSpace(workspaceScript)) return "workspace";
        return null;
    }

    private static IReadOnlyDictionary<string, string> ResolveHeaderSources(
        Collection collection, IReadOnlyList<Folder> folderChain, RequestItem request)
    {
        // Last-write-wins, so we walk in the same order as MergeHeaders and overwrite the
        // attribution as later layers add their own. Skip request-layer entries (they're owned).
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requestNames = new HashSet<string>(
            request.Headers.Where(h => h.Enabled).Select(h => h.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var h in collection.Headers)
            if (h.Enabled && !string.IsNullOrEmpty(h.Name) && !requestNames.Contains(h.Name))
                sources[h.Name] = $"collection “{collection.Name}”";
        foreach (var folder in folderChain)
            foreach (var h in folder.Headers)
                if (h.Enabled && !string.IsNullOrEmpty(h.Name) && !requestNames.Contains(h.Name))
                    sources[h.Name] = $"folder “{folder.Name}”";
        return sources;
    }

    /// <param name="collection">Root collection whose request is being executed.</param>
    /// <param name="folderChain">Outer-most folder first, innermost last. Empty for a request
    /// directly in the collection root.</param>
    /// <param name="request">The leaf request item.</param>
    public static Composed Compose(
        Collection collection,
        IReadOnlyList<Folder> folderChain,
        RequestItem request)
        => Compose(collection, folderChain, request, WorkspaceContext.Empty);

    /// <summary>Compose with a workspace layer underneath the collection. Variables apply
    /// first (so collection / folder / request override them); scripts concatenate first
    /// (so workspace pre-request runs before collection's, etc.).</summary>
    public static Composed Compose(
        Collection collection,
        IReadOnlyList<Folder> folderChain,
        RequestItem request,
        WorkspaceContext workspace)
    {
        var headers = MergeHeaders(collection, folderChain, request);
        var auth = ResolveAuth(collection, folderChain, request);
        var vars = MergeVars(collection, folderChain, request, workspace.Variables);
        var preScript = JoinScripts(
            workspace.PreRequestScript,
            collection.PreRequestScript,
            folderChain.Select(f => f.PreRequestScript),
            request.PreRequestScript);
        var postScript = JoinScripts(
            workspace.PostResponseScript,
            collection.PostResponseScript,
            folderChain.Select(f => f.PostResponseScript),
            request.PostResponseScript);
        var testsScript = JoinScripts(
            workspace.TestsScript,
            collection.TestsScript,
            folderChain.Select(f => f.TestsScript),
            request.Tests);

        return new Composed(headers, auth, vars, preScript, postScript, testsScript, request.Docs);
    }

    // ---- helpers ----

    private static IReadOnlyList<KvPair> MergeHeaders(
        Collection collection, IReadOnlyList<Folder> folderChain, RequestItem request)
    {
        // Use a case-insensitive dict so "X-Trace" + "x-trace" collapse to one entry, with the
        // *latest* casing winning (Bruno does the same — last source decides display).
        var byName = new Dictionary<string, KvPair>(StringComparer.OrdinalIgnoreCase);

        void Apply(IEnumerable<KvPair> source)
        {
            foreach (var h in source)
            {
                if (!h.Enabled) { byName.Remove(h.Name); continue; }
                if (string.IsNullOrEmpty(h.Name)) continue;
                byName[h.Name] = h;
            }
        }

        Apply(collection.Headers);
        foreach (var folder in folderChain) Apply(folder.Headers);
        Apply(request.Headers);

        return byName.Values.ToList();
    }

    private static AuthConfig? ResolveAuth(
        Collection collection, IReadOnlyList<Folder> folderChain, RequestItem request)
    {
        // Request → innermost folder → … → outermost folder → collection.
        if (HasConcreteAuth(request.Auth)) return request.Auth;
        for (var i = folderChain.Count - 1; i >= 0; i--)
        {
            if (HasConcreteAuth(folderChain[i].Auth)) return folderChain[i].Auth;
        }
        if (HasConcreteAuth(collection.Auth)) return collection.Auth;
        // Nothing concrete anywhere — return None (not Inherit, which would re-trigger the walk).
        return null;
    }

    /// <summary>An auth config "counts" if it's set and not Inherit. None is also a concrete
    /// "no auth" choice that ends the walk.</summary>
    private static bool HasConcreteAuth(AuthConfig? auth)
    {
        if (auth is null) return false;
        return auth.Type != AuthType.Inherit;
    }

    private static IReadOnlyDictionary<string, string> MergeVars(
        Collection collection,
        IReadOnlyList<Folder> folderChain,
        RequestItem request,
        IReadOnlyList<KvPair>? workspaceVars)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (workspaceVars is not null) ApplyVars(dict, workspaceVars);
        ApplyVars(dict, collection.Variables);
        foreach (var folder in folderChain) ApplyVars(dict, folder.Variables);
        ApplyVars(dict, request.PreRequestVars);
        return dict;
    }

    private static void ApplyVars(Dictionary<string, string> sink, IEnumerable<KvPair> source)
    {
        foreach (var v in source)
        {
            if (!v.Enabled || string.IsNullOrEmpty(v.Name)) continue;
            sink[v.Name] = v.Value;
        }
    }

    /// <summary>Concatenates non-empty layers with double-newline separators so each layer's
    /// statements are well-terminated even if the user forgets a trailing semicolon.
    /// Order matches the merge order: workspace → collection → folders → request.</summary>
    private static string? JoinScripts(
        string? workspaceScript,
        string? collectionScript,
        IEnumerable<string?> folderScripts,
        string? requestScript)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceScript)) parts.Add(workspaceScript);
        if (!string.IsNullOrWhiteSpace(collectionScript)) parts.Add(collectionScript);
        foreach (var f in folderScripts)
            if (!string.IsNullOrWhiteSpace(f)) parts.Add(f);
        if (!string.IsNullOrWhiteSpace(requestScript)) parts.Add(requestScript);
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
