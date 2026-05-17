using Vegha.Core.Scripting;

namespace Vegha.Core.Requests;

/// <summary>
/// Bridges <see cref="Vegha.Core.Scripting.IBruRequestExecutor"/> to the real
/// <see cref="HttpExecutor"/> so user scripts can call <c>bru.sendRequest({ ... })</c>.
/// Synchronous to match Jint's execution model — Jint is single-threaded and runs
/// scripts to completion, so the host blocks on the executor here.
/// </summary>
public sealed class BruExecutorAdapter : IBruRequestExecutor
{
    private readonly HttpExecutor _executor;

    public BruExecutorAdapter(HttpExecutor executor)
    {
        _executor = executor;
    }

    public BruRequestResult Send(BruRequestOptions options)
    {
        if (string.IsNullOrEmpty(options.Url))
            return new BruRequestResult(0, "Empty URL", string.Empty, 0, Array.Empty<KeyValuePair<string, string>>(), "bru.sendRequest: url is required");

        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri))
            return new BruRequestResult(0, "Invalid URL", string.Empty, 0, Array.Empty<KeyValuePair<string, string>>(), $"bru.sendRequest: invalid url '{options.Url}'");

        var headers = options.Headers is null
            ? new List<KeyValuePair<string, string>>()
            : options.Headers.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();

        var req = new HttpExecutionRequest(
            new HttpMethod(string.IsNullOrEmpty(options.Method) ? "GET" : options.Method),
            uri,
            Headers: headers,
            Body: options.Body,
            ContentType: options.ContentType);

        // Block synchronously — Jint runs single-threaded and the script is already
        // in a worker thread by the time the host invokes us. ConfigureAwait(false)
        // avoids deadlocks with any UI sync context.
        var result = _executor.ExecuteAsync(req).ConfigureAwait(false).GetAwaiter().GetResult();

        return new BruRequestResult(
            Status: result.StatusCode,
            StatusText: result.ReasonPhrase ?? string.Empty,
            Body: result.Body ?? string.Empty,
            ResponseTimeMs: result.ElapsedMilliseconds,
            Headers: result.Headers,
            Error: result.ErrorMessage);
    }
}
