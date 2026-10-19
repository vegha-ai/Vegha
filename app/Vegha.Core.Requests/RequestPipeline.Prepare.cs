using Vegha.Core.Domain;

namespace Vegha.Core.Requests;

public static partial class RequestPipeline
{
    /// <summary>A request resolved for a streaming session (WebSocket, gRPC, GraphQL
    /// subscription): everything a send would compute up to the wire, without sending.</summary>
    public sealed record Prepared(
        /// <summary>Absolute target. ws(s):// for WebSocket, http(s):// for gRPC.</summary>
        Uri? Url,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        /// <summary>The interpolated request body — the first message to send.</summary>
        string? Body,
        /// <summary>The effective variable bag, for interpolating later messages.</summary>
        IReadOnlyDictionary<string, string> Variables,
        IReadOnlyList<string> UnresolvedVariables,
        string? Error)
    {
        public OAuth2TokenResult? OAuth2Token { get; init; }
    }

    /// <summary>Resolves URL, headers (collection/folder/request + auth, including an OAuth2
    /// token) and body for a streaming request, using the same variable precedence as
    /// <see cref="ExecuteAsync"/>. Linked requests and scripts don't run — a session has no
    /// single response for post-response logic to act on.</summary>
    public static async Task<Prepared> PrepareAsync(Inputs inputs, CancellationToken ct = default, Options? options = null)
    {
        options ??= Options.Default;
        var composed = RequestComposition.Compose(
            inputs.Collection, inputs.FolderChain, inputs.Request,
            inputs.Workspace ?? RequestComposition.WorkspaceContext.Empty);
        IReadOnlyDictionary<string, string> vars = Merge(
            inputs.EnvironmentVariables,
            inputs.IterationVariables,
            composed.Vars,
            inputs.RuntimeVariables ?? EmptyVars,
            ToDict(inputs.Request.PreRequestVars));

        Prepared Fail(string error) => new(null, Array.Empty<KeyValuePair<string, string>>(), null, vars, Array.Empty<string>(), error);

        if (options.ResolveSecrets is { } resolveSecrets)
        {
            try { vars = await resolveSecrets(vars, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Fail($"Failed to resolve secrets: {ex.Message}"); }
        }

        var url = ComposeUrl(inputs.Request.Url, inputs.Request.Params, inputs.Request.PathParams, vars);
        if (string.IsNullOrWhiteSpace(url)) return Fail("URL is empty.");
        url = NormalizeStreamingScheme(url, inputs.Request.Kind);

        var configuredAuth = composed.Auth ?? inputs.Request.Auth;
        var authToApply = configuredAuth;
        OAuth2TokenResult? token = null;
        if (configuredAuth is { Type: AuthType.OAuth2 })
        {
            var oauth = await PipelineOAuth2
                .ResolveAsync(configuredAuth, vars, ct, options.OAuth2, options.AllowInteractiveAuth)
                .ConfigureAwait(false);
            token = oauth.Token;
            if (oauth.Error is not null) return Fail(oauth.Error) with { OAuth2Token = token };
            authToApply = oauth.Auth;
        }
        var auth = AuthApplier.Apply(authToApply, url, vars);
        if (!Uri.TryCreate(auth.Url, UriKind.Absolute, out var uri))
            return Fail($"URL is not a valid absolute URI: {auth.Url}") with { OAuth2Token = token };

        var headers = ComposeHeaders(composed.Headers, vars);
        foreach (var h in auth.Headers) headers.Add(h);
        var body = BodyComposer.Compose(inputs.Request.Body, vars, options.BasePath).Body;

        return new Prepared(uri, headers, body, vars, PlaceholderScanner.Collect(uri.ToString(), headers, body), null)
        {
            OAuth2Token = token,
        };
    }

    /// <summary>WebSocket: http(s) → ws(s). gRPC: grpc(s)/bare host:port → http(s) (a bare
    /// address is plaintext, as with local development servers).</summary>
    public static string NormalizeStreamingScheme(string url, RequestKind kind)
    {
        var u = url.Trim();
        static bool Has(string s, string scheme) => s.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase);
        static string Swap(string s, string from, string to) => to + s[from.Length..];
        return kind switch
        {
            RequestKind.WebSocket when Has(u, "https") => Swap(u, "https", "wss"),
            RequestKind.WebSocket when Has(u, "http") => Swap(u, "http", "ws"),
            RequestKind.WebSocket when !u.Contains("://", StringComparison.Ordinal) => "ws://" + u,
            RequestKind.Grpc when Has(u, "grpcs") => Swap(u, "grpcs", "https"),
            RequestKind.Grpc when Has(u, "grpc") => Swap(u, "grpc", "http"),
            RequestKind.Grpc when !u.Contains("://", StringComparison.Ordinal) => "http://" + u,
            _ => u,
        };
    }
}
