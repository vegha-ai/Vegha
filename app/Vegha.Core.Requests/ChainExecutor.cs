using System.Collections.Concurrent;
using Vegha.Core.Domain;
using Vegha.Core.Interpolation;
using Vegha.Core.Scripting;
using Vegha.Core.Scripting.Testing;

namespace Vegha.Core.Requests;

/// <summary>Session-scoped memory of the last response each request produced, keyed by its
/// collection-relative path. Feeds <see cref="ChainRunMode.WhenStale"/> linked requests and the
/// editor's live capture / assertion previews. Thread-safe.</summary>
public sealed class LastResponseCache
{
    public sealed record Entry(ResponseSnapshot Response, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    public LastResponseCache(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public DateTimeOffset Now => _clock();

    public void Record(string path, ResponseSnapshot response) =>
        _entries[RequestComposition.NormalizeChainPath(path)] = new Entry(response, _clock());

    public Entry? Get(string path) =>
        _entries.TryGetValue(RequestComposition.NormalizeChainPath(path), out var e) ? e : null;

    public void Clear() => _entries.Clear();
}

/// <summary>Everything linked-request execution needs beyond the request itself.</summary>
public sealed class ChainContext
{
    public const int MaxDepth = 5;

    public ChainContext(Collection collection, string? collectionRoot, LastResponseCache? cache = null)
    {
        Collection = collection;
        CollectionRoot = collectionRoot ?? InferRoot(collection);
        Cache = cache ?? new LastResponseCache();
    }

    /// <summary>Recovers the collection directory from any loaded request's
    /// <see cref="RequestItem.SourcePath"/>: its directory, walked up once per enclosing folder.
    /// Null for purely in-memory collections.</summary>
    public static string? InferRoot(Collection collection)
    {
        foreach (var (req, chain) in EnumerateRequests(collection))
        {
            if (string.IsNullOrEmpty(req.SourcePath)) continue;
            var dir = Path.GetDirectoryName(req.SourcePath);
            for (var i = 0; i < chain.Count && dir is not null; i++) dir = Path.GetDirectoryName(dir);
            return dir;
        }
        return null;
    }

    public Collection Collection { get; }
    /// <summary>Absolute collection directory, used to turn <see cref="RequestItem.SourcePath"/>
    /// into the relative path stored in <c>chain:pre-request</c>. Null for in-memory collections
    /// (paths are then derived from folder + request names).</summary>
    public string? CollectionRoot { get; }
    public LastResponseCache Cache { get; }

    /// <summary>Relative paths currently executing, outermost first — for cycle detection.</summary>
    public IReadOnlyList<string> Stack { get; private init; } = Array.Empty<string>();

    /// <summary>A copy with <paramref name="path"/> appended to the executing stack (cycle detection).</summary>
    public ChainContext Push(string path) => new(Collection, CollectionRoot, Cache) { Stack = Stack.Append(path).ToList() };

    /// <summary>The collection-relative path of <paramref name="request"/> (forward slashes).</summary>
    public string PathOf(RequestItem request, IReadOnlyList<Folder> folderChain)
    {
        if (!string.IsNullOrEmpty(CollectionRoot) && !string.IsNullOrEmpty(request.SourcePath))
        {
            var rel = Path.GetRelativePath(CollectionRoot, request.SourcePath);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                return RequestComposition.NormalizeChainPath(rel);
        }
        return string.Join('/', folderChain.Select(f => f.Name).Append(request.Name + ".bru"));
    }

    /// <summary>Finds a request by relative path (case-insensitive), falling back to a unique
    /// request-name match so hand-written <c>Login: always</c> entries work too.</summary>
    public (RequestItem Request, IReadOnlyList<Folder> FolderChain)? Resolve(string path)
    {
        var wanted = RequestComposition.NormalizeChainPath(path);
        (RequestItem, IReadOnlyList<Folder>)? byName = null;
        var nameMatches = 0;
        foreach (var (req, chain) in Enumerate())
        {
            if (string.Equals(PathOf(req, chain), wanted, StringComparison.OrdinalIgnoreCase))
                return (req, chain);
            var bare = wanted.EndsWith(".bru", StringComparison.OrdinalIgnoreCase) ? wanted[..^4] : wanted;
            if (string.Equals(req.Name, bare, StringComparison.OrdinalIgnoreCase))
            {
                byName = (req, chain);
                nameMatches++;
            }
        }
        return nameMatches == 1 ? byName : null;
    }

    /// <summary>Every request in the collection with its folder chain (outermost first).</summary>
    public IEnumerable<(RequestItem Request, IReadOnlyList<Folder> FolderChain)> Enumerate() =>
        EnumerateRequests(Collection);

    public static IEnumerable<(RequestItem Request, IReadOnlyList<Folder> FolderChain)> EnumerateRequests(Collection collection)
    {
        foreach (var r in collection.Requests) yield return (r, Array.Empty<Folder>());
        foreach (var f in collection.Folders)
            foreach (var x in Walk(f, new List<Folder>())) yield return x;
    }

    private static IEnumerable<(RequestItem, IReadOnlyList<Folder>)> Walk(Folder folder, List<Folder> parents)
    {
        var chain = new List<Folder>(parents) { folder };
        foreach (var r in folder.Requests) yield return (r, chain);
        foreach (var sub in folder.Folders)
            foreach (var x in Walk(sub, chain)) yield return x;
    }
}

/// <summary>One linked request's outcome, shown in the response pane's chain strip.</summary>
public sealed record ChainStep(
    string Path,
    string Name,
    bool Ran,
    string? SkipReason,
    int StatusCode,
    long ElapsedMilliseconds,
    string? Error,
    IReadOnlyDictionary<string, string> Captured,
    IReadOnlyList<ChainStep> Nested)
{
    public bool Failed => Error is not null;
}

/// <summary>Variables produced by a chain run plus the per-link steps. <see cref="Error"/>
/// is set when a link failed and the dependent request must not be sent.</summary>
public sealed record ChainResult(
    IReadOnlyList<ChainStep> Steps,
    IReadOnlyDictionary<string, string> RuntimeVariables,
    IReadOnlyDictionary<string, string> EnvVariables,
    IReadOnlyDictionary<string, string> GlobalVariables,
    string? Error)
{
    public static readonly ChainResult Empty = new(Array.Empty<ChainStep>(),
        new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(), null);
}

/// <summary>
/// Runs linked requests (<c>chain:pre-request</c>) before the request that depends on them.
/// Each link runs through <see cref="RequestPipeline"/> — so its own captures, assertions and
/// linked requests apply — and the variables it produces flow into the next link and finally
/// into the dependent request. Cycles and depth beyond <see cref="ChainContext.MaxDepth"/> fail
/// the chain with a readable message.
/// </summary>
public static class ChainExecutor
{
    public static async Task<ChainResult> RunAsync(
        IReadOnlyList<KvPair> links,
        ChainContext context,
        IReadOnlyDictionary<string, string> environmentVariables,
        IReadOnlyDictionary<string, string> runtimeVariables,
        HttpExecutor http,
        JintHost scripting,
        RequestComposition.WorkspaceContext? workspace = null,
        CancellationToken ct = default)
    {
        if (links.Count == 0) return ChainResult.Empty;

        var steps = new List<ChainStep>();
        var runtime = new Dictionary<string, string>(StringComparer.Ordinal);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var global = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var link in links)
        {
            if (!link.Enabled || string.IsNullOrWhiteSpace(link.Name)) continue;
            ct.ThrowIfCancellationRequested();

            var path = RequestComposition.NormalizeChainPath(link.Name);
            var spec = ChainLinkSpec.Parse(link.Value);
            var resolved = context.Resolve(path);
            if (resolved is null)
                return Fail(steps, runtime, env, global, path, path, $"Linked request '{path}' was not found in the collection");

            var (request, folderChain) = resolved.Value;
            var canonical = context.PathOf(request, folderChain);

            if (context.Stack.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                return Fail(steps, runtime, env, global, canonical, request.Name,
                    $"Linked request cycle: {string.Join(" → ", context.Stack.Append(canonical))}");
            if (context.Stack.Count >= ChainContext.MaxDepth)
                return Fail(steps, runtime, env, global, canonical, request.Name,
                    $"Linked requests nest deeper than {ChainContext.MaxDepth} levels");

            // Variables as the link sees them: env (+ what earlier links persisted) under the
            // runtime layer (+ what earlier links captured).
            var envNow = Merge(environmentVariables, env, global);
            var runtimeNow = Merge(runtimeVariables, runtime);
            var visible = Merge(envNow, runtimeNow);

            var skip = SkipReason(spec, canonical, visible, context.Cache);
            if (skip is not null)
            {
                steps.Add(new ChainStep(canonical, request.Name, false, skip, 0, 0, null,
                    new Dictionary<string, string>(), Array.Empty<ChainStep>()));
                continue;
            }

            var outputs = await RequestPipeline.ExecuteAsync(
                new RequestPipeline.Inputs(
                    context.Collection, folderChain, request,
                    EnvironmentVariables: envNow,
                    IterationVariables: runtimeNow,
                    Workspace: workspace,
                    Chain: context.Push(canonical)),
                http, scripting, ct).ConfigureAwait(false);

            var captured = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in outputs.Captures.Where(c => c.Applied)) captured[c.Name] = c.Value.ToVariableString();

            string? error = outputs.ErrorMessage;
            if (error is null && outputs.StatusCode >= 400)
                error = $"{outputs.StatusCode} {outputs.ReasonPhrase}".Trim();
            if (error is null && outputs.FailedTests > 0)
                error = $"{outputs.FailedTests} assertion(s) failed";

            steps.Add(new ChainStep(canonical, request.Name, true, null, outputs.StatusCode,
                outputs.ElapsedMilliseconds, error, captured, outputs.ChainSteps));

            if (error is not null)
                return new ChainResult(steps, runtime, env, global,
                    $"Linked request '{request.Name}' failed: {error}");

            foreach (var (k, v) in outputs.RuntimeVariableMutations) runtime[k] = v;
            foreach (var (k, v) in outputs.EnvVarMutations) env[k] = v;
            foreach (var (k, v) in outputs.GlobalVarMutations) global[k] = v;
        }

        return new ChainResult(steps, runtime, env, global, null);
    }

    /// <summary>Why a link doesn't need to run, or null to run it.</summary>
    public static string? SkipReason(ChainLinkSpec spec, string path,
        IReadOnlyDictionary<string, string> vars, LastResponseCache cache)
    {
        switch (spec.Mode)
        {
            case ChainRunMode.WhenEmpty:
            {
                var name = spec.Argument.Trim().TrimStart('{').TrimEnd('}').Trim();
                return vars.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v)
                    ? $"{{{{{name}}}}} is already set"
                    : null;
            }
            case ChainRunMode.WhenStale:
            {
                var last = cache.Get(path);
                if (last is null) return null;
                var age = cache.Now - last.At;
                return age < spec.MaxAge ? $"last response is {FormatAge(age)} old" : null;
            }
            case ChainRunMode.When:
            {
                var condition = Interpolator.Resolve(spec.Argument, vars);
                FilterCondition parsed;
                try { parsed = FilterCondition.Parse(condition); }
                catch (FilterException) { return null; } // malformed → run (fail open, visible in the step list)
                var holds = FilterCondition.Compare(ResolvedValue.Of(parsed.Key), parsed.Operator, parsed.Value);
                return holds ? null : $"condition '{condition}' is false";
            }
            default:
                return null;
        }
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalSeconds < 60 ? $"{(int)age.TotalSeconds}s"
        : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m"
        : $"{(int)age.TotalHours}h";

    private static ChainResult Fail(List<ChainStep> steps,
        Dictionary<string, string> runtime, Dictionary<string, string> env, Dictionary<string, string> global,
        string path, string name, string error)
    {
        steps.Add(new ChainStep(path, name, false, null, 0, 0, error,
            new Dictionary<string, string>(), Array.Empty<ChainStep>()));
        return new ChainResult(steps, runtime, env, global, error);
    }

    private static Dictionary<string, string> Merge(params IReadOnlyDictionary<string, string>[] bags)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bag in bags)
            foreach (var (k, v) in bag) merged[k] = v;
        return merged;
    }
}
