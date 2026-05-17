namespace Vegha.Core.Requests;

/// <summary>The outcome of an HTTP request executed by <see cref="HttpExecutor"/>.</summary>
public sealed record HttpExecutionResult(
    int StatusCode,
    string ReasonPhrase,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string Body,
    long ElapsedMilliseconds,
    string? ErrorMessage = null,
    HttpExecutionTiming? Timing = null,
    /// <summary>Raw response bytes — needed by binary previews (image, PDF) where the
    /// UTF-8 string in <see cref="Body"/> is lossy. Empty for transport failures.</summary>
    byte[]? BodyBytes = null,
    /// <summary>Content-Type from the response headers (lower-cased). Convenient when the
    /// caller wants to switch on it without re-parsing headers.</summary>
    string? ContentType = null,
    /// <summary>HTTP-style rendering of the outgoing request (request line + headers + body)
    /// captured just before send. The view shows this on a "Sent" subtab so the user can
    /// see exactly what crossed the wire — invaluable for debugging server-side errors that
    /// don't match what the codegen panel suggests.</summary>
    string? SentRequestText = null)
{
    public bool IsSuccess => ErrorMessage is null && StatusCode is >= 200 and < 300;
    public bool IsTransportError => ErrorMessage is not null;

    public static HttpExecutionResult Failure(string error, long elapsedMs) =>
        new(0, string.Empty, Array.Empty<KeyValuePair<string, string>>(), string.Empty, elapsedMs, error, Timing: HttpExecutionTiming.Zero);
}
