namespace Vegha.Core.Requests;

/// <summary>
/// Per-phase timing for an HTTP request. All values in milliseconds.
/// Phases are sequential: DNS → TCP connect → TLS handshake → TTFB (server processing) → content download.
/// Phases that did not occur (cached connection, plaintext, etc.) report 0.
/// </summary>
public sealed record HttpExecutionTiming(
    double DnsMs,
    double ConnectMs,
    double TlsMs,
    double TtfbMs,
    double ContentMs,
    double TotalMs)
{
    public static HttpExecutionTiming Zero { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>Sum of measured phases (sanity check: TotalMs should be ~equal).</summary>
    public double SumOfPhases => DnsMs + ConnectMs + TlsMs + TtfbMs + ContentMs;
}
