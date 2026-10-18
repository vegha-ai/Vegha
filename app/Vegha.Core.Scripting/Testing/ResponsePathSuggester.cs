using System.Text.Json.Nodes;

namespace Vegha.Core.Scripting.Testing;

/// <summary>Builds autocomplete suggestions for the capture / assertion path box from the
/// last response, so users pick <c>data.items[0].id</c> instead of remembering it.</summary>
public static class ResponsePathSuggester
{
    /// <summary>Every friendly path in a JSON document, breadth-first, capped at
    /// <paramref name="max"/>. Arrays contribute <c>[0]</c> (first element) and <c>[-1]</c>.</summary>
    public static IReadOnlyList<string> JsonPaths(JsonNode? root, int max = 400)
    {
        var result = new List<string>();
        if (root is null) return result;
        var queue = new Queue<(JsonNode Node, List<FriendlyPath.Segment> Path)>();
        queue.Enqueue((root, new List<FriendlyPath.Segment>()));
        while (queue.Count > 0 && result.Count < max)
        {
            var (node, path) = queue.Dequeue();
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                    {
                        var p = new List<FriendlyPath.Segment>(path) { new(key, null, false) };
                        result.Add(FriendlyPath.Format(p));
                        if (result.Count >= max) break;
                        if (child is JsonObject or JsonArray) queue.Enqueue((child, p));
                    }
                    break;
                case JsonArray arr when arr.Count > 0:
                {
                    var first = new List<FriendlyPath.Segment>(path) { new(null, 0, false) };
                    result.Add(FriendlyPath.Format(first));
                    if (arr.Count > 1) result.Add(FriendlyPath.Format(new List<FriendlyPath.Segment>(path) { new(null, -1, false) }));
                    if (arr[0] is JsonObject or JsonArray) queue.Enqueue((arr[0]!, first));
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>Suggestions for one source: JSON paths, header names, or cookie names.</summary>
    public static IReadOnlyList<string> For(ResponseSource source, ResponseSnapshot? response)
    {
        if (response is null) return Array.Empty<string>();
        return source switch
        {
            ResponseSource.Body => response.IsJson ? JsonPaths(response.Json) : Array.Empty<string>(),
            ResponseSource.JsonPath => response.IsJson ? JsonPaths(response.Json).Select(p => p.StartsWith('[') ? "$" + p : "$." + p).ToList() : Array.Empty<string>(),
            ResponseSource.Header => response.Headers.Select(h => h.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ResponseSource.Cookie => response.Cookies.Keys.ToList(),
            _ => Array.Empty<string>(),
        };
    }
}
