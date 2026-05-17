namespace Vegha.Core.Scripting;

/// <summary>
/// Indirection for <c>bru.sendRequest</c> — lets pre/post-response scripts fire
/// HTTP requests through the host's HttpExecutor without Core.Scripting taking a
/// dependency on Core.Requests. The host wires a small adapter that delegates to
/// HttpExecutor.
/// </summary>
public interface IBruRequestExecutor
{
    /// <summary>Synchronously executes a sub-request from a script. Returns the
    /// <see cref="ResponseApi"/>-shaped result the script will see.</summary>
    BruRequestResult Send(BruRequestOptions options);
}

/// <summary>What a script passes to <c>bru.sendRequest</c>. Headers and body are
/// optional; method defaults to GET when omitted.</summary>
public sealed class BruRequestOptions
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = string.Empty;
    public IDictionary<string, string>? Headers { get; set; }
    public string? Body { get; set; }
    public string? ContentType { get; set; }
}

/// <summary>Result delivered back to the script. Mirrors <see cref="ResponseApi"/>
/// shape so scripts that already use <c>res</c>-shape feel familiar.</summary>
public sealed record BruRequestResult(
    int Status,
    string StatusText,
    string Body,
    long ResponseTimeMs,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Error);
