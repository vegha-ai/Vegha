using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Vegha.Core.Requests;

/// <summary>
/// HTTP request executor with per-phase timing capture (DNS, TCP, TLS, TTFB, content).
/// Uses <see cref="SocketsHttpHandler"/> with custom <c>ConnectCallback</c> + <c>PlaintextStreamFilter</c>
/// to mark phase boundaries; phases that didn't occur (connection reuse, plaintext, etc.) report 0.
/// </summary>
public sealed class HttpExecutor
{
    private static readonly HttpRequestOptionsKey<RequestTiming> TimingKey = new("Vegha.RequestTiming");

    private TimeSpan _timeout;
    private HttpClient _defaultClient;
    private readonly CookieJarStore? _cookieStore;
    private X509Certificate2Collection _trustedCAs;
    private IWebProxy? _proxy;
    private bool _cacheSslSessions = true;
    private long _maxBodyBytes = 50L * 1024 * 1024;

    /// <summary>Shared per-executor cookie jar — persists across requests within the app session.</summary>
    public System.Net.CookieContainer CookieJar { get; }

    public HttpExecutor(HttpClient client) : this(client, cookieStore: null, trustedCAs: null) { }

    public HttpExecutor(HttpClient client, CookieJarStore? cookieStore)
        : this(client, cookieStore, trustedCAs: null) { }

    /// <summary>Optionally accepts a <see cref="CookieJarStore"/> so cookies survive app restarts —
    /// the store's container is shared with the HTTP handler and snapshotted to SQLite after each request.
    /// <paramref name="trustedCAs"/> seeds the custom-CA trust store used as a fallback when system
    /// validation rejects the server cert (corporate CA chains land here).</summary>
    public HttpExecutor(HttpClient client, CookieJarStore? cookieStore, X509Certificate2Collection? trustedCAs)
    {
        _timeout = client.Timeout == TimeSpan.Zero ? TimeSpan.FromSeconds(100) : client.Timeout;
        _cookieStore = cookieStore;
        _trustedCAs = trustedCAs ?? new X509Certificate2Collection();
        CookieJar = cookieStore?.Container ?? new System.Net.CookieContainer();
        _defaultClient = new HttpClient(BuildHandler(true, true, true, CookieJar)) { Timeout = _timeout };
    }

    /// <summary>Hot-swap the trust list (called by the host on Settings save). Affects new
    /// per-request handlers immediately; the default client keeps its construction-time list
    /// until the host rebuilds the executor on app restart.</summary>
    public void UpdateTrustedCAs(X509Certificate2Collection trustedCAs)
    {
        _trustedCAs = trustedCAs;
        RebuildDefaultClient();
    }

    /// <summary>Hot-swap the proxy. Null = no proxy. Affects subsequent requests immediately.</summary>
    public void UpdateProxy(IWebProxy? proxy)
    {
        _proxy = proxy;
        RebuildDefaultClient();
    }

    /// <summary>Toggle TLS connection reuse. When false, the pooled-connection lifetime is set to
    /// zero so each request opens a fresh TCP/TLS session.</summary>
    public void UpdateCacheSslSessions(bool enabled)
    {
        _cacheSslSessions = enabled;
        RebuildDefaultClient();
    }

    /// <summary>Update the request timeout. Affects subsequent requests.</summary>
    public void UpdateTimeout(TimeSpan timeout)
    {
        _timeout = timeout == TimeSpan.Zero ? TimeSpan.FromSeconds(100) : timeout;
        RebuildDefaultClient();
    }

    /// <summary>Cap on response body size in bytes. Reads larger than this are truncated and
    /// the response is marked as truncated via a trailing ellipsis byte sentinel handled by the UI.</summary>
    public void UpdateMaxBodyBytes(long maxBytes)
    {
        _maxBodyBytes = maxBytes <= 0 ? long.MaxValue : maxBytes;
    }

    /// <summary>Discard the pooled connections in the default client. Used by the
    /// Network → "Clear SSL session cache" button.</summary>
    public void ResetConnectionPool() => RebuildDefaultClient();

    private void RebuildDefaultClient()
    {
        try { _defaultClient?.Dispose(); } catch { /* best-effort */ }
        _defaultClient = new HttpClient(BuildHandler(true, true, true, CookieJar)) { Timeout = _timeout };
    }

    public async Task<HttpExecutionResult> ExecuteAsync(
        HttpExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        // The cookie jar is hydrated on a background task at startup so DI build doesn't
        // block on SQLite. Wait for that load before the first send so persisted session
        // cookies actually attach to the outgoing request.
        if (_cookieStore is not null)
            await _cookieStore.ReadyAsync.ConfigureAwait(false);
        var timing = new RequestTiming();
        var decompression = ResolveDecompression(request.Headers);
        var (client, ownedClient) = GetClientFor(request.Options, decompression);
        string sentRequestText = string.Empty;
        try
        {
            using var msg = BuildRequestMessage(request);
            msg.Options.Set(TimingKey, timing);
            timing.IsHttps = string.Equals(request.Url.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            // Snapshot the outgoing request as HTTP text BEFORE send so it's preserved even
            // when the call later throws (transport errors etc.).
            sentRequestText = await RenderOutgoingRequestAsync(msg, request.Body, decompression, cancellationToken)
                .ConfigureAwait(false);

            timing.MarkStart();
            using var resp = await client.SendAsync(
                msg,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            timing.MarkResponseHeaders();

            var bodyBytes = await ReadCappedAsync(resp, _maxBodyBytes, cancellationToken).ConfigureAwait(false);
            // Decode for the text view; keep raw bytes for image/PDF previews.
            var body = bodyBytes.Length == 0 ? string.Empty
                : System.Text.Encoding.UTF8.GetString(bodyBytes);
            timing.MarkCompleted();

            var headers = new List<KeyValuePair<string, string>>();
            AppendHeaders(headers, resp.Headers);
            AppendHeaders(headers, resp.Content.Headers);
            var contentType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();

            // Persist cookies after each response (best-effort). Failures here must not block the
            // user-visible response — if the disk write fails, log and move on.
            if (_cookieStore is not null)
            {
                try { await _cookieStore.PersistAsync(cancellationToken).ConfigureAwait(false); }
                catch { /* best-effort; cookies stay in memory */ }
            }

            return new HttpExecutionResult(
                StatusCode: (int)resp.StatusCode,
                ReasonPhrase: resp.ReasonPhrase ?? string.Empty,
                Headers: headers,
                Body: body,
                ElapsedMilliseconds: (long)timing.Snapshot().TotalMs,
                Timing: timing.Snapshot(),
                BodyBytes: bodyBytes,
                ContentType: contentType,
                SentRequestText: sentRequestText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            timing.MarkCompleted();
            return new HttpExecutionResult(
                0,
                string.Empty,
                Array.Empty<KeyValuePair<string, string>>(),
                string.Empty,
                (long)timing.Snapshot().TotalMs,
                ex.Message,
                timing.Snapshot(),
                SentRequestText: sentRequestText);
        }
        finally
        {
            ownedClient?.Dispose();
        }
    }

    /// <summary>Returns the HttpClient to use plus an owned client (if a one-off was constructed for this request).</summary>
    private (HttpClient Client, HttpClient? Owned) GetClientFor(
        HttpRequestOptions? options, System.Net.DecompressionMethods decompression)
    {
        var followRedirects = options?.FollowRedirects ?? true;
        var verifySsl = options?.VerifySsl ?? true;
        var useCookies = options?.UseCookies ?? true;
        var ntlm = options?.NtlmCredential;
        var clientCert = options?.ClientCertificate;

        // If the override happens to match defaults and there's no NTLM creds + no mTLS cert,
        // reuse the shared client. A non-default decompression set (user supplied their own
        // Accept-Encoding header) needs its own handler.
        if (followRedirects && verifySsl && useCookies && ntlm is null && clientCert is null
            && decompression == System.Net.DecompressionMethods.All)
            return (_defaultClient, null);

        // NTLM requires HttpClientHandler.Credentials — SocketsHttpHandler doesn't expose
        // per-request creds the same way. We use HttpClientHandler when NTLM is set; for
        // non-NTLM tweaks we keep the per-phase-timing SocketsHttpHandler path.
        HttpMessageHandler handler;
        if (ntlm is not null)
        {
            var ntlmHandler = new HttpClientHandler
            {
                AllowAutoRedirect = followRedirects,
                UseCookies = useCookies,
                CookieContainer = useCookies ? CookieJar : new System.Net.CookieContainer(),
                Credentials = ntlm,
                PreAuthenticate = false, // let server challenge first
                ServerCertificateCustomValidationCallback = verifySsl ? null : (_, _, _, _) => true,
                // Same parity as the SocketsHttpHandler path — NTLM endpoints often return
                // gzipped responses too and the user expects Postman-like wire sizes.
                AutomaticDecompression = decompression,
            };
            if (clientCert is not null)
            {
                ntlmHandler.ClientCertificates.Add(clientCert);
                ntlmHandler.ClientCertificateOptions = ClientCertificateOption.Manual;
            }
            handler = ntlmHandler;
        }
        else
        {
            var sockets = BuildHandler(followRedirects, verifySsl, useCookies, CookieJar, decompression);
            if (clientCert is not null)
            {
                sockets.SslOptions.ClientCertificates ??=
                    new System.Security.Cryptography.X509Certificates.X509CertificateCollection();
                sockets.SslOptions.ClientCertificates.Add(clientCert);
            }
            handler = sockets;
        }
        var oneOff = new HttpClient(handler) { Timeout = _timeout };
        return (oneOff, oneOff);
    }

    private SocketsHttpHandler BuildHandler(
        bool followRedirects, bool verifySsl, bool useCookies, System.Net.CookieContainer cookieJar,
        System.Net.DecompressionMethods decompression = System.Net.DecompressionMethods.All)
    {
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            UseCookies = useCookies,
            CookieContainer = useCookies ? cookieJar : new System.Net.CookieContainer(),
            ConnectCallback = ConnectAsync,
            PlaintextStreamFilter = PlaintextStreamFilter,
            // Default (no user Accept-Encoding header): match Postman / browsers /
            // curl --compressed — advertise gzip+deflate+brotli and transparently
            // decompress. Without this, servers that would have returned a 1 MB gzipped
            // body instead return the full 15 MB uncompressed payload (some users saw
            // exactly this with their notification-tree endpoint). When the user supplies
            // their own Accept-Encoding header, the flags mirror exactly the encodings
            // they listed — otherwise .NET's DecompressionHandler appends the missing
            // tokens (e.g. "br") to the header, which some gateways (Apigee) reject with
            // protocol.http.UnsupportedEncoding.
            AutomaticDecompression = decompression,
        };
        if (_proxy is not null)
        {
            h.Proxy = _proxy;
            h.UseProxy = true;
        }
        else
        {
            h.UseProxy = false;
        }
        if (!_cacheSslSessions)
        {
            // Force a fresh TCP/TLS handshake every request — kills the connection pool.
            h.PooledConnectionLifetime = TimeSpan.Zero;
            h.PooledConnectionIdleTimeout = TimeSpan.Zero;
        }
        if (!verifySsl)
        {
            h.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        else
        {
            // Snapshot the current CA list so a later UpdateTrustedCAs doesn't rewire
            // already-built handlers mid-flight.
            var caSnapshot = _trustedCAs;
            if (caSnapshot.Count > 0)
            {
                h.SslOptions.RemoteCertificateValidationCallback =
                    (_, cert, chain, errors) => ValidateAgainstSystemOrCustomCAs(cert, errors, caSnapshot);
            }
        }
        return h;
    }

    /// <summary>Accepts the cert if either the system's built-in chain validates it (via the
    /// <paramref name="errors"/> argument), or a chain rooted in the custom trust store does.
    /// This way custom CAs add to the system's defaults rather than replace them.</summary>
    private static bool ValidateAgainstSystemOrCustomCAs(
        System.Security.Cryptography.X509Certificates.X509Certificate? cert,
        SslPolicyErrors errors,
        X509Certificate2Collection trustedCAs)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert is null) return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustedCAs);
        return chain.Build(new X509Certificate2(cert));
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        ctx.InitialRequestMessage.Options.TryGetValue(TimingKey, out var t);

        var dnsStart = Stopwatch.GetTimestamp();
        IPAddress[] addresses;
        if (IPAddress.TryParse(ctx.DnsEndPoint.Host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
        }
        t?.MarkDnsResolved(dnsStart);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            var connectStart = Stopwatch.GetTimestamp();
            await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
            t?.MarkConnected(connectStart);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static ValueTask<Stream> PlaintextStreamFilter(
        SocketsHttpPlaintextStreamFilterContext ctx, CancellationToken ct)
    {
        if (ctx.InitialRequestMessage.Options.TryGetValue(TimingKey, out var t))
        {
            t?.MarkPlaintextReady();
        }
        return new ValueTask<Stream>(ctx.PlaintextStream);
    }

    /// <summary>Picks a Content-Type for a file based on its extension. Used for file body
    /// uploads and multipart file parts when no explicit content type was supplied. Falls
    /// back to <c>application/octet-stream</c> for unknown extensions.</summary>
    private static string GuessContentTypeFromExtension(string path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant();
        return ext switch
        {
            ".json"  => "application/json",
            ".xml"   => "application/xml",
            ".txt"   => "text/plain",
            ".html" or ".htm" => "text/html",
            ".css"   => "text/css",
            ".js"    => "application/javascript",
            ".csv"   => "text/csv",
            ".pdf"   => "application/pdf",
            ".png"   => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"   => "image/gif",
            ".svg"   => "image/svg+xml",
            ".webp"  => "image/webp",
            ".bmp"   => "image/bmp",
            ".ico"   => "image/x-icon",
            ".mp3"   => "audio/mpeg",
            ".wav"   => "audio/wav",
            ".mp4"   => "video/mp4",
            ".zip"   => "application/zip",
            ".gz"    => "application/gzip",
            ".tar"   => "application/x-tar",
            _        => "application/octet-stream"
        };
    }

    private static HttpRequestMessage BuildRequestMessage(HttpExecutionRequest request)
    {
        var msg = new HttpRequestMessage(request.Method, request.Url);

        // Body precedence: FilePath > MultipartFields > FormFields > Body. First non-empty
        // wins. Each structured option picks its own Content-Type (multipart/form-data with
        // boundary, application/x-www-form-urlencoded, or the file's MIME). The user can
        // still override via a Content-Type header — that override is applied below.
        if (!string.IsNullOrEmpty(request.FilePath) && File.Exists(request.FilePath))
        {
            // Stream the file rather than reading it all into memory — supports large uploads
            // without the full payload sitting on the heap. The stream is owned by the
            // StreamContent and gets disposed when msg.Content is disposed.
            var stream = File.OpenRead(request.FilePath);
            msg.Content = new StreamContent(stream);
            var mime = string.IsNullOrWhiteSpace(request.ContentType)
                ? GuessContentTypeFromExtension(request.FilePath)
                : request.ContentType;
            if (!string.IsNullOrEmpty(mime) &&
                System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(mime, out var fmt))
            {
                msg.Content.Headers.ContentType = fmt;
            }
        }
        else if (request.MultipartFields is { Count: > 0 } mp)
        {
            // MultipartFormDataContent picks its own boundary; never pre-set Content-Type
            // because that would strip the boundary parameter and confuse the server.
            var multipart = new MultipartFormDataContent();
            foreach (var field in mp)
            {
                if (string.IsNullOrEmpty(field.Name)) continue;
                if (string.Equals(field.Kind, "file", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(field.Value) && File.Exists(field.Value))
                {
                    var fs = File.OpenRead(field.Value);
                    var fc = new StreamContent(fs);
                    var partMime = string.IsNullOrWhiteSpace(field.ContentType)
                        ? GuessContentTypeFromExtension(field.Value)
                        : field.ContentType!;
                    if (!string.IsNullOrEmpty(partMime) &&
                        System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(partMime, out var pmt))
                    {
                        fc.Headers.ContentType = pmt;
                    }
                    multipart.Add(fc, field.Name, Path.GetFileName(field.Value));
                }
                else
                {
                    var sc = new StringContent(field.Value ?? string.Empty, Encoding.UTF8);
                    sc.Headers.ContentType = null; // suppress default text/plain;charset=utf-8
                    if (!string.IsNullOrWhiteSpace(field.ContentType) &&
                        System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(field.ContentType, out var tmt))
                    {
                        sc.Headers.ContentType = tmt;
                    }
                    multipart.Add(sc, field.Name);
                }
            }
            msg.Content = multipart;
        }
        else if (request.FormFields is { Count: > 0 } ff)
        {
            msg.Content = new FormUrlEncodedContent(ff);
        }
        else if (request.Body is not null)
        {
            msg.Content = new StringContent(request.Body, Encoding.UTF8);
            // StringContent's 2-arg ctor sets ContentType=text/plain; charset=utf-8 via the
            // strongly-typed property. Headers.Remove("Content-Type") doesn't clear that
            // typed property — only assigning ContentType=null does. Without this, the
            // typed property AND any TryAddWithoutValidation entry both serialize, producing
            // "text/plain; charset=utf-8, application/json".
            msg.Content.Headers.ContentType = null;
        }

        var userContentType = request.Headers?
            .FirstOrDefault(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            .Value;

        // Pick the final Content-Type:
        //   - User-supplied header wins, parsed verbatim (no auto-charset injection)
        //     EXCEPT for multipart bodies (the boundary parameter must survive — overriding
        //     wipes it and the server gets garbled multipart) and FormUrlEncodedContent
        //     (which already self-sets the correct type with charset).
        //   - Otherwise fall back to request.ContentType + auto-append charset=utf-8 since
        //     that's the conventional JSON/text behaviour servers expect when the caller
        //     didn't say otherwise.
        // Either way it's set via the typed property exactly once.
        var skipUserOverride = msg.Content is MultipartFormDataContent or FormUrlEncodedContent;
        if (msg.Content is not null)
        {
            if (!skipUserOverride && !string.IsNullOrWhiteSpace(userContentType)
                && System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(userContentType, out var userMt))
            {
                msg.Content.Headers.ContentType = userMt;
            }
            else if (!skipUserOverride && !string.IsNullOrWhiteSpace(request.ContentType)
                && System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var defMt))
            {
                if (string.IsNullOrEmpty(defMt.CharSet)) defMt.CharSet = "utf-8";
                msg.Content.Headers.ContentType = defMt;
            }
        }

        if (request.Headers is not null)
        {
            foreach (var (name, value) in request.Headers)
            {
                // Content-Type is already handled above via the typed property — skip here
                // to avoid the dual-value bug.
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsContentHeader(name))
                {
                    msg.Content ??= new StringContent(string.Empty);
                    // Remove first so duplicates from the StringContent ctor (or earlier
                    // adds in this loop) don't accumulate as comma-separated values.
                    msg.Content.Headers.Remove(name);
                    msg.Content.Headers.TryAddWithoutValidation(name, value);
                }
                else
                {
                    msg.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        return msg;
    }

    private static void AppendHeaders(
        List<KeyValuePair<string, string>> sink,
        HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            sink.Add(new KeyValuePair<string, string>(header.Key, string.Join(", ", header.Value)));
        }
    }

    private static bool IsContentHeader(string name) =>
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps the user's Accept-Encoding header (if any) to the decompression flags the
    /// handler should use. No header → All (the Postman-parity default: gzip, deflate, br).
    /// With a header, only the encodings the user listed are enabled, so the handler sends the
    /// header verbatim instead of appending unlisted tokens — without this, a request with
    /// "Accept-Encoding: gzip" still goes out as "gzip, deflate, br" and gateways that don't
    /// support brotli reject it.</summary>
    private static System.Net.DecompressionMethods ResolveDecompression(
        IReadOnlyList<KeyValuePair<string, string>>? headers)
    {
        var accept = headers?
            .FirstOrDefault(h => string.Equals(h.Key, "Accept-Encoding", StringComparison.OrdinalIgnoreCase))
            .Value;
        if (string.IsNullOrWhiteSpace(accept))
            return System.Net.DecompressionMethods.All;

        var methods = System.Net.DecompressionMethods.None;
        foreach (var raw in accept.Split(','))
        {
            // Strip q-values: "gzip;q=0.8" → "gzip".
            var token = raw.Split(';')[0].Trim();
            if (token.Equals("gzip", StringComparison.OrdinalIgnoreCase)
                || token.Equals("x-gzip", StringComparison.OrdinalIgnoreCase))
                methods |= System.Net.DecompressionMethods.GZip;
            else if (token.Equals("deflate", StringComparison.OrdinalIgnoreCase))
                methods |= System.Net.DecompressionMethods.Deflate;
            else if (token.Equals("br", StringComparison.OrdinalIgnoreCase))
                methods |= System.Net.DecompressionMethods.Brotli;
            // identity / * / unknown tokens still go on the wire, but the handler can't
            // decompress them, so they contribute no flag.
        }
        return methods;
    }

    /// <summary>Renders the outgoing request as HTTP-style text (request line + headers +
    /// blank line + body) so the user can compare it byte-for-byte against the curl they
    /// run from the terminal. Used by the "Sent" subtab on the response viewer.</summary>
    private static async Task<string> RenderOutgoingRequestAsync(
        HttpRequestMessage msg,
        string? originalBody,
        System.Net.DecompressionMethods decompression,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        // Request line
        sb.Append(msg.Method.Method).Append(' ').Append(msg.RequestUri).Append(" HTTP/")
          .Append(msg.Version).AppendLine();
        // Host header is implicit on HttpRequestMessage; render it for completeness.
        if (msg.RequestUri is { } uri)
            sb.Append("Host: ").AppendLine(uri.Authority);
        // Request headers (non-content)
        foreach (var h in msg.Headers)
            sb.Append(h.Key).Append(": ").AppendLine(string.Join(", ", h.Value));
        // Accept-Encoding is injected at send time by .NET's DecompressionHandler (per the
        // AutomaticDecompression flags), so it isn't on msg.Headers yet — render the value
        // the wire will carry. When the user set the header themselves it's already above.
        if (msg.Headers.AcceptEncoding.Count == 0 && !msg.Headers.Contains("Accept-Encoding"))
        {
            var tokens = new List<string>(3);
            if (decompression.HasFlag(System.Net.DecompressionMethods.GZip)) tokens.Add("gzip");
            if (decompression.HasFlag(System.Net.DecompressionMethods.Deflate)) tokens.Add("deflate");
            if (decompression.HasFlag(System.Net.DecompressionMethods.Brotli)) tokens.Add("br");
            if (tokens.Count > 0)
                sb.Append("Accept-Encoding: ").AppendLine(string.Join(", ", tokens));
        }
        // Content headers
        if (msg.Content is not null)
        {
            foreach (var h in msg.Content.Headers)
                sb.Append(h.Key).Append(": ").AppendLine(string.Join(", ", h.Value));
        }
        sb.AppendLine();
        // Body — prefer the originally-supplied string (preserves user's exact
        // whitespace) over re-reading the StringContent stream.
        if (!string.IsNullOrEmpty(originalBody))
        {
            sb.Append(originalBody);
        }
        else if (msg.Content is not null)
        {
            try { sb.Append(await msg.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); }
            catch { /* best-effort; binary or already-consumed content */ }
        }
        return sb.ToString();
    }

    /// <summary>Reads up to <paramref name="maxBytes"/> bytes from the response content. When the
    /// body exceeds the cap, the read stops at the boundary and the remainder is discarded —
    /// the caller still sees the bytes that were already consumed (preserves preview rendering).
    /// Using a manual streamed copy avoids buffering the entire response in memory when the
    /// server returns a huge payload and the user only wants the first few MB.</summary>
    private static async Task<byte[]> ReadCappedAsync(
        HttpResponseMessage resp, long maxBytes, CancellationToken ct)
    {
        if (maxBytes == long.MaxValue)
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Cap how much we accumulate in memory. We deliberately bound the destination
        // capacity to maxBytes so an attacker-controlled Content-Length can't OOM us.
        var initialCapacity = (int)Math.Min(maxBytes, 64 * 1024);
        using var dst = new MemoryStream(initialCapacity);
        var buf = new byte[16 * 1024];
        long total = 0;
        while (total < maxBytes)
        {
            var toRead = (int)Math.Min(buf.Length, maxBytes - total);
            var n = await src.ReadAsync(buf.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (n == 0) break;
            dst.Write(buf, 0, n);
            total += n;
        }
        return dst.ToArray();
    }

    /// <summary>Per-request timing accumulator. Phase timestamps are filled by the connect callbacks
    /// and the executor's send/read points; missing phases (e.g., reused connection) report 0.</summary>
    private sealed class RequestTiming
    {
        private long _start;
        private long? _dnsCompleted;
        private long? _connectCompleted;
        private long? _plaintextReady;
        private long? _responseHeaders;
        private long? _completed;

        /// <summary>True when the request URL scheme is https — gates whether the
        /// (connect → plaintext-ready) gap is reported as TLS or as 0 (plaintext bookkeeping).</summary>
        public bool IsHttps { get; set; }

        public void MarkStart() => _start = Stopwatch.GetTimestamp();

        public void MarkDnsResolved(long dnsStartTs)
        {
            // Record the time spent resolving DNS, anchored at dnsStartTs to capture only that phase.
            _dnsCompleted = Stopwatch.GetTimestamp();
            // Stash the DNS-start timestamp into a deltable form by encoding both endpoints.
            _dnsStart = dnsStartTs;
        }

        private long _dnsStart;

        public void MarkConnected(long connectStartTs)
        {
            _connectCompleted = Stopwatch.GetTimestamp();
            _connectStart = connectStartTs;
        }

        private long _connectStart;

        public void MarkPlaintextReady() => _plaintextReady = Stopwatch.GetTimestamp();
        public void MarkResponseHeaders() => _responseHeaders = Stopwatch.GetTimestamp();
        public void MarkCompleted() => _completed = Stopwatch.GetTimestamp();

        public HttpExecutionTiming Snapshot()
        {
            // DNS phase: resolved - started (only when DNS happened on this request)
            var dnsMs = _dnsCompleted.HasValue ? ToMs(_dnsCompleted.Value - _dnsStart) : 0;
            // Connect phase: connected - connectStart
            var connectMs = _connectCompleted.HasValue ? ToMs(_connectCompleted.Value - _connectStart) : 0;
            // TLS phase: plaintextReady - connected — only meaningful for HTTPS; the gap on
            // plaintext HTTP is just SocketsHttpHandler bookkeeping, not a real handshake.
            var tlsMs = IsHttps && _plaintextReady.HasValue && _connectCompleted.HasValue
                ? Math.Max(0, ToMs(_plaintextReady.Value - _connectCompleted.Value))
                : 0;
            // TTFB: responseHeaders - last available pre-send marker.
            var preSend = _plaintextReady ?? _connectCompleted ?? _start;
            var ttfbMs = _responseHeaders.HasValue ? Math.Max(0, ToMs(_responseHeaders.Value - preSend)) : 0;
            // Content: completed - responseHeaders
            var contentMs = _completed.HasValue && _responseHeaders.HasValue
                ? Math.Max(0, ToMs(_completed.Value - _responseHeaders.Value))
                : 0;
            // Total: completed - start
            var totalMs = _completed.HasValue ? ToMs(_completed.Value - _start) : 0;

            return new HttpExecutionTiming(dnsMs, connectMs, tlsMs, ttfbMs, contentMs, totalMs);
        }

        private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
