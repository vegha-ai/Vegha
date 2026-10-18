using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vegha.Core.Domain;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Core.Exporters;

/// <summary>File-name and JSON helpers shared by every format exporter.</summary>
internal static class ExportNaming
{
    /// <summary>Same rule as <c>BruCollectionWriter.Sanitize</c>: invalid file-name chars
    /// become <c>_</c>, blank names become "untitled". Also strips '/' and '\' on every OS
    /// so a name never introduces an extra zip directory level.</summary>
    public static string Sanitize(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? string.Empty)
            .Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c)
            .ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }

    /// <summary>Returns <paramref name="stem"/>, or <c>stem-2</c>, <c>stem-3</c>, … — the
    /// first variant not yet in <paramref name="used"/> (case-insensitive).</summary>
    public static string Unique(HashSet<string> used, string stem)
    {
        if (used.Add(stem)) return stem;
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}-{i}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        // Keep {{var}}, quotes in scripts, and non-ASCII readable in the output instead of
        // \u-escaping them — the files are meant to be diffed and hand-inspected.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] ToUtf8(JsonNode node) =>
        System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(_jsonOptions));

    public static byte[] ToUtf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}

/// <summary>Secret handling for exported environments.</summary>
public static class EnvironmentExportHelper
{
    /// <summary>Blanks the value of every variable named in
    /// <see cref="DomainEnv.SecretVariables"/> unless <paramref name="includeSecrets"/> is set.
    /// The secret flag itself is kept so the target tool can still mark the variable secret.</summary>
    public static DomainEnv Redact(DomainEnv env, bool includeSecrets)
    {
        if (includeSecrets || env.SecretVariables.Count == 0) return env;
        var secret = new HashSet<string>(env.SecretVariables, StringComparer.Ordinal);
        return env with
        {
            Variables = env.Variables
                .Select(v => secret.Contains(v.Name) ? v with { Value = string.Empty } : v)
                .ToList(),
        };
    }
}

/// <summary>Inheritance + URL helpers for formats that can't express Vegha's
/// collection → folder → request chain natively.</summary>
internal static class ExportTree
{
    /// <summary>Total request count across the collection's folder tree.</summary>
    public static int CountRequests(Collection c)
    {
        var n = c.Requests.Count;
        void Walk(IList<Folder> folders)
        {
            foreach (var f in folders) { n += f.Requests.Count; Walk(f.Folders); }
        }
        Walk(c.Folders);
        return n;
    }

    /// <summary>Headers the request actually sends: collection, then each folder, then the
    /// request — later same-name (case-insensitive) entries win, matching execution order.</summary>
    public static List<KvPair> EffectiveHeaders(Collection c, IReadOnlyList<Folder> chain, RequestItem r)
    {
        var merged = new List<KvPair>();
        void Apply(IEnumerable<KvPair> headers)
        {
            foreach (var h in headers)
            {
                merged.RemoveAll(x => string.Equals(x.Name, h.Name, StringComparison.OrdinalIgnoreCase));
                merged.Add(h);
            }
        }
        Apply(c.Headers);
        foreach (var f in chain) Apply(f.Headers);
        Apply(r.Headers);
        return merged;
    }

    /// <summary>Auth the request resolves to: its own, else the nearest folder's, else the
    /// collection's. Null/Inherit at a level means "defer to the parent".</summary>
    public static AuthConfig? EffectiveAuth(Collection c, IReadOnlyList<Folder> chain, RequestItem r)
    {
        if (IsConcrete(r.Auth)) return r.Auth;
        for (var i = chain.Count - 1; i >= 0; i--)
            if (IsConcrete(chain[i].Auth)) return chain[i].Auth;
        return IsConcrete(c.Auth) ? c.Auth : null;
    }

    public static bool IsConcrete(AuthConfig? auth) => auth is not null && auth.Type != AuthType.Inherit;

    /// <summary>The URL without its query string / fragment — used by formats that keep
    /// query params separately and would otherwise duplicate them.</summary>
    public static string StripQuery(string url)
    {
        var i = url.IndexOfAny(new[] { '?', '#' });
        return i < 0 ? url : url[..i];
    }

    /// <summary>True for request kinds every HTTP-centric target format can represent.</summary>
    public static bool IsHttpLike(RequestItem r) =>
        r.Kind is RequestKind.Http or RequestKind.GraphQL or RequestKind.Soap;
}
