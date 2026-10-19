using Vegha.Core.Domain;
using Vegha.Core.Interpolation;

namespace Vegha.Core.Requests;

/// <summary>
/// Turns a <see cref="BodyConfig"/> into the body shape <see cref="HttpExecutionRequest"/>
/// carries, interpolating <c>{{var}}</c> placeholders on the way. Covers every
/// <see cref="BodyMode"/>: raw text modes travel as <see cref="Composed.Body"/>, multipart and
/// binary-file bodies as the executor's structured <see cref="Composed.MultipartFields"/> /
/// <see cref="Composed.FilePath"/> slots.
/// </summary>
public static class BodyComposer
{
    /// <summary>The composed body. At most one of <see cref="Body"/>,
    /// <see cref="MultipartFields"/> and <see cref="FilePath"/> is set.</summary>
    public sealed record Composed(
        string? Body,
        string? ContentType,
        IReadOnlyList<MultipartField>? MultipartFields = null,
        string? FilePath = null)
    {
        public static readonly Composed None = new(null, null);
    }

    /// <param name="basePath">Directory that relative multipart-file and binary-body paths
    /// resolve against (the collection root). Null leaves relative paths untouched.</param>
    public static Composed Compose(
        BodyConfig body,
        IReadOnlyDictionary<string, string> vars,
        string? basePath = null)
    {
        switch (body.Mode)
        {
            case BodyMode.Json: return Raw(body.Content, "application/json", vars);
            case BodyMode.Xml: return Raw(body.Content, "application/xml", vars);
            case BodyMode.Text: return Raw(body.Content, "text/plain", vars);
            case BodyMode.Sparql: return Raw(body.Content, "application/sparql-query", vars);
            case BodyMode.FormUrlEncoded:
            {
                var enabled = body.FormData.Where(f => f.Enabled && !string.IsNullOrEmpty(f.Name)).ToList();
                var encoded = string.Join("&", enabled.Select(f =>
                    Uri.EscapeDataString(Interpolator.Resolve(f.Name, vars)) + "=" +
                    Uri.EscapeDataString(Interpolator.Resolve(f.Value, vars))));
                return new Composed(encoded, "application/x-www-form-urlencoded");
            }
            case BodyMode.MultipartForm:
            {
                // The executor builds the boundary and Content-Type header itself.
                var fields = body.MultipartItems
                    .Where(m => m.Enabled && !string.IsNullOrEmpty(m.Name))
                    .Select(m =>
                    {
                        var value = Interpolator.Resolve(m.Value, vars);
                        var isFile = string.Equals(m.Kind, "file", StringComparison.OrdinalIgnoreCase);
                        return new MultipartField(
                            Interpolator.Resolve(m.Name, vars),
                            isFile ? ResolvePath(value, basePath) : value,
                            isFile ? "file" : "text",
                            string.IsNullOrEmpty(m.ContentType) ? null : m.ContentType);
                    })
                    .ToList();
                return new Composed(null, null, MultipartFields: fields);
            }
            case BodyMode.Binary:
            {
                if (string.IsNullOrWhiteSpace(body.FilePath)) return Composed.None;
                var path = ResolvePath(Interpolator.Resolve(body.FilePath, vars), basePath);
                var contentType = string.IsNullOrEmpty(body.FileContentType) ? null : body.FileContentType;
                return new Composed(null, contentType, FilePath: path);
            }
            case BodyMode.GraphQL:
            {
                var query = body.GraphQLQuery ?? string.Empty;
                if (string.IsNullOrWhiteSpace(query)) return Composed.None;
                var resolvedQuery = Interpolator.Resolve(query, vars);
                // Multi-operation documents need an operationName or the server rejects the
                // request — send the first named operation (same rule the editor and codegen apply).
                var opName = Vegha.Core.GraphQL.GraphQLDocumentAnalyzer.ResolveOperationNameForSend(resolvedQuery);
                return new Composed(
                    GraphQLJson(resolvedQuery, Interpolator.Resolve(body.GraphQLVariables ?? string.Empty, vars), opName),
                    "application/json");
            }
            default:
                return Composed.None;
        }
    }

    /// <summary>The <c>{"query", "operationName"?, "variables"}</c> envelope. Variables that
    /// don't look like JSON are replaced with <c>{}</c>; the server rejects anything malformed
    /// inside them, so users see exactly what they typed.</summary>
    public static string GraphQLJson(string query, string variables, string? operationName)
    {
        var trimmed = variables.TrimStart();
        var varsJson = trimmed.StartsWith('{') || trimmed.StartsWith('[') ? variables : "{}";
        return "{\"query\":" + System.Text.Json.JsonSerializer.Serialize(query) +
               (operationName is null
                   ? string.Empty
                   : ",\"operationName\":" + System.Text.Json.JsonSerializer.Serialize(operationName)) +
               ",\"variables\":" + varsJson + "}";
    }

    private static Composed Raw(string? content, string contentType, IReadOnlyDictionary<string, string> vars) =>
        string.IsNullOrEmpty(content)
            ? new Composed(null, contentType)
            : new Composed(Interpolator.Resolve(content, vars), contentType);

    private static string ResolvePath(string path, string? basePath) =>
        string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(path) || Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(basePath, path));
}
