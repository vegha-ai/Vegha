using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Domain;
using Vegha.Core.Requests;
using DomainAuthType = Vegha.Core.Domain.AuthType;

namespace Vegha.App.ViewModels;

/// <summary>Where an auth block is defined. Only affects which types are offerable and the
/// wording of the inheritance hints — the editing surface and persistence are identical.</summary>
public enum AuthScope
{
    Request,
    Folder,
    Collection,
}

/// <summary>
/// The one auth editing surface, shared by the request editor, the folder properties tab, and
/// the collection settings tab. Every level gets the same per-type forms, the same OAuth2 token
/// panel, and the same <see cref="AuthConfig"/> round-trip — which is what makes collection- or
/// folder-level auth behave identically to request-level auth at send time.
///
/// Hosts wire three seams:
///   <see cref="VariablesProvider"/> — the {{var}} snapshot for highlighting + token fetches,
///   <see cref="InheritedAuthProvider"/> — resolves what this scope would inherit (Override),
///   <see cref="Changed"/> — any user edit, for the host's dirty flag.
/// </summary>
public partial class AuthSectionViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<string, string> NoVariables =
        new Dictionary<string, string>();

    /// <summary>Every type the editor knows how to configure, in display order.</summary>
    public static readonly IReadOnlyList<string> AllAuthTypes = new[]
    {
        "none", "inherit", "apikey", "bearer", "basic", "digest", "ntlm", "oauth1", "oauth2", "awsv4", "wsse"
    };

    public static readonly IReadOnlyList<string> OAuth1SignatureMethods = new[]
    {
        "HMAC-SHA1", "HMAC-SHA256", "HMAC-SHA512", "PLAINTEXT"
    };

    public static readonly IReadOnlyList<string> OAuth2GrantTypes = new[]
    {
        "client_credentials", "password", "authorization_code"
    };

    public static readonly IReadOnlyList<string> OAuth2CredentialPlacements = new[]
    {
        "body", "basic_auth_header"
    };

    public static readonly IReadOnlyList<string> ApiKeyPlacements = new[]
    {
        "header", "queryparams"
    };

    public static readonly IReadOnlyList<string> OAuth2TokenSources = new[]
    {
        "access_token", "id_token", "refresh_token"
    };

    public static readonly IReadOnlyList<string> OAuth2AddTokenToOptions = new[]
    {
        "headers", "queryparams", "body"
    };

    public static readonly IReadOnlyList<string> OAuth2AdditionalParamSendIn = new[]
    {
        "body", "headers", "queryparams"
    };

    private readonly OAuth2TokenAcquirer? _oauth2;

    public AuthScope Scope { get; }

    /// <summary>A collection is the top of the inheritance chain, so "inherit" has nothing to
    /// resolve against — it's dropped from the picker there. Every other type is offered at
    /// every level: OAuth2 in particular is most useful defined once on the collection.</summary>
    public IReadOnlyList<string> AvailableAuthTypes { get; }

    /// <summary>What "none" means here — a request sends unauthenticated; a folder or
    /// collection simply contributes nothing for its children to inherit.</summary>
    public string NoneDescription => Scope switch
    {
        AuthScope.Request => "No authentication will be applied to this request.",
        AuthScope.Folder => "This folder contributes no auth. Requests inside it fall through to the collection (or an outer folder).",
        _ => "This collection contributes no auth. Requests inherit nothing unless they set their own.",
    };

    /// <summary>What "inherit" means here. Resolution is real: RequestComposition walks
    /// request → innermost folder → outermost folder → collection and the nearest concrete
    /// block wins, whatever its type.</summary>
    public string InheritDescription => Scope switch
    {
        AuthScope.Request => "Auth comes from the nearest parent that defines one — the innermost folder, else the collection. Every type inherits, including OAuth 2.0, AWS SigV4, Digest and NTLM.",
        _ => "This folder defers to its parent — an outer folder, else the collection.",
    };

    public IReadOnlyList<string> AvailableApiKeyPlacements => ApiKeyPlacements;
    public IReadOnlyList<string> AvailableOAuth1SignatureMethods => OAuth1SignatureMethods;
    public IReadOnlyList<string> AvailableOAuth2GrantTypes => OAuth2GrantTypes;
    public IReadOnlyList<string> AvailableOAuth2CredentialPlacements => OAuth2CredentialPlacements;
    public IReadOnlyList<string> AvailableOAuth2TokenSources => OAuth2TokenSources;
    public IReadOnlyList<string> AvailableOAuth2AddTokenTo => OAuth2AddTokenToOptions;
    public IReadOnlyList<string> AvailableOAuth2ParamSendIn => OAuth2AdditionalParamSendIn;

    public AuthSectionViewModel(AuthScope scope = AuthScope.Request, OAuth2TokenAcquirer? oauth2 = null)
    {
        Scope = scope;
        _oauth2 = oauth2;
        AvailableAuthTypes = scope == AuthScope.Collection
            ? AllAuthTypes.Where(t => t != "inherit").ToList()
            : AllAuthTypes;

        WireRowTracking(OAuth2TokenParameters);
        WireRowTracking(OAuth2RefreshParameters);

        // Ghost-row UX: the trailing blank row spawns the next as you type.
        IsLoading = true;
        try
        {
            KvAutoAppend.EnsureTrailingBlank(OAuth2TokenParameters, () => new OAuth2AdditionalParameter(), r => r.IsBlank);
            KvAutoAppend.EnsureTrailingBlank(OAuth2RefreshParameters, () => new OAuth2AdditionalParameter(), r => r.IsBlank);
        }
        finally { IsLoading = false; }

        KvAutoAppend.Wire(OAuth2TokenParameters, () => new OAuth2AdditionalParameter(), r => r.IsBlank, () => IsLoading);
        KvAutoAppend.Wire(OAuth2RefreshParameters, () => new OAuth2AdditionalParameter(), r => r.IsBlank, () => IsLoading);
    }

    // ---- Host seams --------------------------------------------------------

    /// <summary>Raised on any user-visible edit to persisted auth state. Runtime-only fields
    /// (the fetched token, its decoded payload, status text, the secret-visibility toggle) are
    /// deliberately excluded — they must not dirty the request.</summary>
    public event EventHandler? Changed;

    /// <summary>Set by the host while it populates fields from disk, so the load doesn't
    /// register as a user edit.</summary>
    public bool IsLoading { get; set; }

    /// <summary>Supplies the variable snapshot used for {{var}} highlighting in the editors and
    /// for interpolation when fetching a token.</summary>
    public Func<IReadOnlyDictionary<string, string>>? VariablesProvider { get; set; }

    public IReadOnlyDictionary<string, string> ResolvedVariablesSnapshot =>
        VariablesProvider?.Invoke() ?? NoVariables;

    /// <summary>Call when the host's variable set changed so the editors re-highlight.</summary>
    public void RefreshVariablesSnapshot() => OnPropertyChanged(nameof(ResolvedVariablesSnapshot));

    /// <summary>Resolves the auth this scope would inherit if it declared none. Drives the
    /// Override action. Null (or a null result) means nothing is inherited.</summary>
    public Func<AuthConfig?>? InheritedAuthProvider { get; set; }

    /// <summary>Human-readable source of the inherited auth ("collection “Acme”"), or null
    /// when this scope owns its auth. Set by the host from RequestComposition's source map.</summary>
    [ObservableProperty] private string? _inheritedFrom;

    public bool IsInherited => !string.IsNullOrEmpty(InheritedFrom);

    partial void OnInheritedFromChanged(string? value) => OnPropertyChanged(nameof(IsInherited));

    /// <summary>Raised after <see cref="OverrideInherited"/> materializes the parent's auth,
    /// so the host can recompute its inheritance hints.</summary>
    public event EventHandler? OverrideApplied;

    /// <summary>Copies the inherited config onto this scope, breaking the inheritance link
    /// without touching the parent.</summary>
    [RelayCommand]
    public void OverrideInherited()
    {
        if (!IsInherited) return;
        var inherited = InheritedAuthProvider?.Invoke();
        if (inherited is null) return;
        ApplyAuthConfig(inherited);
        OverrideApplied?.Invoke(this, EventArgs.Empty);
    }

    private void MarkChanged()
    {
        if (!IsLoading) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void WireRowTracking(ObservableCollection<OAuth2AdditionalParameter> rows)
    {
        foreach (var row in rows) row.PropertyChanged += OnRowChanged;
        rows.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (OAuth2AdditionalParameter row in e.NewItems) row.PropertyChanged += OnRowChanged;
            if (e.OldItems is not null)
                foreach (OAuth2AdditionalParameter row in e.OldItems) row.PropertyChanged -= OnRowChanged;
            MarkChanged();
        };
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => MarkChanged();

    // ---- Type ---------------------------------------------------------------

    [ObservableProperty] private string _authType = "none";

    partial void OnAuthTypeChanged(string value)
    {
        MarkChanged();
        OnPropertyChanged(nameof(HasData));
    }

    /// <summary>True when this scope actually configures something — drives the "•" badge on
    /// the Auth tab. "none" and "inherit" don't count as data of their own.</summary>
    public bool HasData =>
        !string.IsNullOrEmpty(AuthType) &&
        !string.Equals(AuthType, "none", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(AuthType, "inherit", StringComparison.OrdinalIgnoreCase);

    // ---- Bearer / Basic / Digest / NTLM ------------------------------------

    [ObservableProperty] private string _bearerToken = string.Empty;
    [ObservableProperty] private string _basicUsername = string.Empty;
    [ObservableProperty] private string _basicPassword = string.Empty;
    [ObservableProperty] private string _digestUsername = string.Empty;
    [ObservableProperty] private string _digestPassword = string.Empty;
    [ObservableProperty] private string _ntlmUsername = string.Empty;
    [ObservableProperty] private string _ntlmPassword = string.Empty;
    [ObservableProperty] private string _ntlmDomain = string.Empty;

    partial void OnBearerTokenChanged(string value) => MarkChanged();
    partial void OnBasicUsernameChanged(string value) => MarkChanged();
    partial void OnBasicPasswordChanged(string value) => MarkChanged();
    partial void OnDigestUsernameChanged(string value) => MarkChanged();
    partial void OnDigestPasswordChanged(string value) => MarkChanged();
    partial void OnNtlmUsernameChanged(string value) => MarkChanged();
    partial void OnNtlmPasswordChanged(string value) => MarkChanged();
    partial void OnNtlmDomainChanged(string value) => MarkChanged();

    // ---- API key -----------------------------------------------------------

    [ObservableProperty] private string _apiKeyName = "X-API-Key";
    [ObservableProperty] private string _apiKeyValue = string.Empty;
    [ObservableProperty] private string _apiKeyPlacement = "header";

    partial void OnApiKeyNameChanged(string value) => MarkChanged();
    partial void OnApiKeyValueChanged(string value) => MarkChanged();
    partial void OnApiKeyPlacementChanged(string value) => MarkChanged();

    // ---- OAuth1 (RFC 5849) -------------------------------------------------

    [ObservableProperty] private string _oAuth1ConsumerKey = string.Empty;
    [ObservableProperty] private string _oAuth1ConsumerSecret = string.Empty;
    [ObservableProperty] private string _oAuth1Token = string.Empty;
    [ObservableProperty] private string _oAuth1TokenSecret = string.Empty;
    [ObservableProperty] private string _oAuth1SignatureMethod = "HMAC-SHA1";
    [ObservableProperty] private string _oAuth1Realm = string.Empty;

    partial void OnOAuth1ConsumerKeyChanged(string value) => MarkChanged();
    partial void OnOAuth1ConsumerSecretChanged(string value) => MarkChanged();
    partial void OnOAuth1TokenChanged(string value) => MarkChanged();
    partial void OnOAuth1TokenSecretChanged(string value) => MarkChanged();
    partial void OnOAuth1SignatureMethodChanged(string value) => MarkChanged();
    partial void OnOAuth1RealmChanged(string value) => MarkChanged();

    // ---- WSSE --------------------------------------------------------------

    [ObservableProperty] private string _wsseUsername = string.Empty;
    [ObservableProperty] private string _wssePassword = string.Empty;

    partial void OnWsseUsernameChanged(string value) => MarkChanged();
    partial void OnWssePasswordChanged(string value) => MarkChanged();

    // ---- AWS SigV4 ---------------------------------------------------------

    [ObservableProperty] private string _awsAccessKeyId = string.Empty;
    [ObservableProperty] private string _awsSecretAccessKey = string.Empty;
    [ObservableProperty] private string _awsRegion = string.Empty;
    [ObservableProperty] private string _awsService = string.Empty;
    [ObservableProperty] private string _awsSessionToken = string.Empty;

    partial void OnAwsAccessKeyIdChanged(string value) => MarkChanged();
    partial void OnAwsSecretAccessKeyChanged(string value) => MarkChanged();
    partial void OnAwsRegionChanged(string value) => MarkChanged();
    partial void OnAwsServiceChanged(string value) => MarkChanged();
    partial void OnAwsSessionTokenChanged(string value) => MarkChanged();

    // ---- OAuth2 ------------------------------------------------------------

    [ObservableProperty] private string _oAuth2GrantType = "client_credentials";
    [ObservableProperty] private string _oAuth2TokenUrl = string.Empty;
    [ObservableProperty] private string _oAuth2ClientId = string.Empty;
    [ObservableProperty] private string _oAuth2ClientSecret = string.Empty;
    [ObservableProperty] private string _oAuth2Scope = string.Empty;
    [ObservableProperty] private string _oAuth2CredentialsPlacement = "body";
    [ObservableProperty] private string _oAuth2Username = string.Empty;
    [ObservableProperty] private string _oAuth2Password = string.Empty;
    [ObservableProperty] private string _oAuth2AuthorizationUrl = string.Empty;
    [ObservableProperty] private string _oAuth2CallbackUrl = "http://127.0.0.1:8765/oauth/callback";
    [ObservableProperty] private string _oAuth2State = string.Empty;
    [ObservableProperty] private bool _oAuth2UsePkce = true;

    /// <summary>Optional second token endpoint used for refresh_token grant calls. Empty falls
    /// back to <see cref="OAuth2TokenUrl"/>. Matches Bruno's "Refresh Token URL".</summary>
    [ObservableProperty] private string _oAuth2RefreshTokenUrl = string.Empty;

    /// <summary>Which field of the token response becomes the request's bearer:
    /// "access_token" (default) / "id_token" / "refresh_token".</summary>
    [ObservableProperty] private string _oAuth2TokenSource = "access_token";

    /// <summary>Cache-isolation label, so multiple clients sharing a (tokenUrl, clientId, scope)
    /// tuple get independent cache slots.</summary>
    [ObservableProperty] private string _oAuth2TokenId = "credentials";

    /// <summary>Where the acquired token rides: "headers" (default) / "queryparams" / "body".</summary>
    [ObservableProperty] private string _oAuth2AddTokenTo = "headers";

    /// <summary>Prefix prepended in the Authorization header. Defaults to "Bearer"; some IdPs
    /// want "JWT", "Token", or nothing.</summary>
    [ObservableProperty] private string _oAuth2HeaderPrefix = "Bearer";

    /// <summary>Send auto-acquires a token when none is cached.</summary>
    [ObservableProperty] private bool _oAuth2AutoFetch = true;

    /// <summary>Refresh transparently before expiry when a refresh token is available.</summary>
    [ObservableProperty] private bool _oAuth2AutoRefresh;

    partial void OnOAuth2GrantTypeChanged(string value) => MarkChanged();
    partial void OnOAuth2TokenUrlChanged(string value) => MarkChanged();
    partial void OnOAuth2ClientIdChanged(string value) => MarkChanged();
    partial void OnOAuth2ClientSecretChanged(string value) => MarkChanged();
    partial void OnOAuth2ScopeChanged(string value) => MarkChanged();
    partial void OnOAuth2CredentialsPlacementChanged(string value) => MarkChanged();
    partial void OnOAuth2UsernameChanged(string value) => MarkChanged();
    partial void OnOAuth2PasswordChanged(string value) => MarkChanged();
    partial void OnOAuth2AuthorizationUrlChanged(string value) => MarkChanged();
    partial void OnOAuth2CallbackUrlChanged(string value) => MarkChanged();
    partial void OnOAuth2StateChanged(string value) => MarkChanged();
    partial void OnOAuth2UsePkceChanged(bool value) => MarkChanged();
    partial void OnOAuth2RefreshTokenUrlChanged(string value) => MarkChanged();
    partial void OnOAuth2TokenSourceChanged(string value) => MarkChanged();
    partial void OnOAuth2TokenIdChanged(string value) => MarkChanged();
    partial void OnOAuth2AddTokenToChanged(string value) => MarkChanged();
    partial void OnOAuth2HeaderPrefixChanged(string value) => MarkChanged();
    partial void OnOAuth2AutoFetchChanged(bool value) => MarkChanged();
    partial void OnOAuth2AutoRefreshChanged(bool value) => MarkChanged();

    // Runtime-only OAuth2 state — never persisted, never dirties the host.
    [ObservableProperty] private bool _oAuth2IsClientSecretVisible;
    [ObservableProperty] private string _oAuth2LastAccessToken = string.Empty;
    [ObservableProperty] private string _oAuth2DecodedPayload = string.Empty;
    [ObservableProperty] private string _oAuth2TokenType = "Bearer";
    [ObservableProperty] private string _oAuth2StatusMessage = string.Empty;

    /// <summary>Free-form rows added to the token request ("Additional Parameters → Token").</summary>
    public ObservableCollection<OAuth2AdditionalParameter> OAuth2TokenParameters { get; } = new();

    /// <summary>Same shape, applied only to refresh_token grant requests.</summary>
    public ObservableCollection<OAuth2AdditionalParameter> OAuth2RefreshParameters { get; } = new();

    // ---- Domain round-trip --------------------------------------------------

    /// <summary>Materializes the editor state into the <see cref="AuthConfig"/> that both the
    /// send path and the file format consume. Identical at every scope — which is the whole
    /// point: a collection's OAuth2 block is byte-for-byte what a request's would be.</summary>
    public AuthConfig? BuildAuthConfig() => AuthType switch
    {
        "none" => null,
        "inherit" => new AuthConfig { Type = DomainAuthType.Inherit },
        "bearer" => new AuthConfig
        {
            Type = DomainAuthType.Bearer,
            Parameters = new Dictionary<string, string> { ["token"] = BearerToken },
        },
        "basic" => new AuthConfig
        {
            Type = DomainAuthType.Basic,
            Parameters = new Dictionary<string, string>
            {
                ["username"] = BasicUsername,
                ["password"] = BasicPassword,
            },
        },
        "digest" => new AuthConfig
        {
            Type = DomainAuthType.Digest,
            Parameters = new Dictionary<string, string>
            {
                ["username"] = DigestUsername,
                ["password"] = DigestPassword,
            },
        },
        "ntlm" => new AuthConfig
        {
            Type = DomainAuthType.Ntlm,
            Parameters = new Dictionary<string, string>
            {
                ["username"] = NtlmUsername,
                ["password"] = NtlmPassword,
                ["domain"] = NtlmDomain,
            },
        },
        "oauth1" => new AuthConfig
        {
            Type = DomainAuthType.OAuth1,
            Parameters = new Dictionary<string, string>
            {
                ["consumerKey"] = OAuth1ConsumerKey,
                ["consumerSecret"] = OAuth1ConsumerSecret,
                ["token"] = OAuth1Token,
                ["tokenSecret"] = OAuth1TokenSecret,
                ["signatureMethod"] = OAuth1SignatureMethod,
                ["realm"] = OAuth1Realm,
            },
        },
        "wsse" => new AuthConfig
        {
            Type = DomainAuthType.Wsse,
            Parameters = new Dictionary<string, string>
            {
                ["username"] = WsseUsername,
                ["password"] = WssePassword,
            },
        },
        "apikey" => new AuthConfig
        {
            Type = DomainAuthType.ApiKey,
            Parameters = new Dictionary<string, string>
            {
                ["key"] = ApiKeyName,
                ["value"] = ApiKeyValue,
                ["placement"] = ApiKeyPlacement,
            },
        },
        "oauth2" => new AuthConfig
        {
            Type = DomainAuthType.OAuth2,
            Parameters = new Dictionary<string, string>
            {
                ["grant_type"] = OAuth2GrantType,
                ["access_token_url"] = OAuth2TokenUrl,
                ["authorization_url"] = OAuth2AuthorizationUrl,
                ["callback_url"] = OAuth2CallbackUrl,
                ["client_id"] = OAuth2ClientId,
                ["client_secret"] = OAuth2ClientSecret,
                ["scope"] = OAuth2Scope,
                ["state"] = OAuth2State,
                ["use_pkce"] = OAuth2UsePkce ? "true" : "false",
                ["username"] = OAuth2Username,
                ["password"] = OAuth2Password,
                ["credentials_placement"] = OAuth2CredentialsPlacement,
                ["refresh_token_url"] = OAuth2RefreshTokenUrl,
                ["token_source"] = OAuth2TokenSource,
                ["token_id"] = OAuth2TokenId,
                ["add_token_to"] = OAuth2AddTokenTo,
                ["header_prefix"] = OAuth2HeaderPrefix,
                ["auto_fetch"] = OAuth2AutoFetch ? "true" : "false",
                ["auto_refresh"] = OAuth2AutoRefresh ? "true" : "false",
                // The additional-parameter tables ride inside the flat string→string
                // dictionary as JSON so every existing file format round-trips unchanged.
                ["additional_token_params"] = AuthParameters.SerializeAdditionalParams(ToRows(OAuth2TokenParameters)),
                ["additional_refresh_params"] = AuthParameters.SerializeAdditionalParams(ToRows(OAuth2RefreshParameters)),
            },
        },
        "awsv4" => new AuthConfig
        {
            Type = DomainAuthType.AwsV4,
            Parameters = new Dictionary<string, string>
            {
                ["accessKeyId"] = AwsAccessKeyId,
                ["secretAccessKey"] = AwsSecretAccessKey,
                ["region"] = AwsRegion,
                ["service"] = AwsService,
                ["sessionToken"] = AwsSessionToken,
            },
        },
        _ => null,
    };

    /// <summary>Populates the editor from a persisted config. Missing keys fall back to the
    /// same defaults a fresh editor starts with, so older files load without surprise flips.</summary>
    public void ApplyAuthConfig(AuthConfig? config)
    {
        if (config is null)
        {
            AuthType = "none";
            return;
        }

        string Get(string key, string fallback = "") =>
            config.Parameters.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
        bool Flag(string key, bool fallback) =>
            config.Parameters.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
                ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                : fallback;

        switch (config.Type)
        {
            case DomainAuthType.Inherit:
                AuthType = "inherit";
                break;
            case DomainAuthType.Bearer:
                AuthType = "bearer";
                BearerToken = Get("token");
                break;
            case DomainAuthType.Basic:
                AuthType = "basic";
                BasicUsername = Get("username");
                BasicPassword = Get("password");
                break;
            case DomainAuthType.Digest:
                AuthType = "digest";
                DigestUsername = Get("username");
                DigestPassword = Get("password");
                break;
            case DomainAuthType.Ntlm:
                AuthType = "ntlm";
                NtlmUsername = Get("username");
                NtlmPassword = Get("password");
                NtlmDomain = Get("domain");
                break;
            case DomainAuthType.OAuth1:
                AuthType = "oauth1";
                OAuth1ConsumerKey = Get("consumerKey");
                OAuth1ConsumerSecret = Get("consumerSecret");
                OAuth1Token = Get("token");
                OAuth1TokenSecret = Get("tokenSecret");
                OAuth1SignatureMethod = Get("signatureMethod", "HMAC-SHA1");
                OAuth1Realm = Get("realm");
                break;
            case DomainAuthType.Wsse:
                AuthType = "wsse";
                WsseUsername = Get("username");
                WssePassword = Get("password");
                break;
            case DomainAuthType.ApiKey:
                AuthType = "apikey";
                ApiKeyName = Get("key", "X-API-Key");
                ApiKeyValue = Get("value");
                ApiKeyPlacement = Get("placement", "header");
                break;
            case DomainAuthType.OAuth2:
                AuthType = "oauth2";
                OAuth2GrantType = Get("grant_type", "client_credentials");
                OAuth2TokenUrl = Get("access_token_url");
                OAuth2AuthorizationUrl = Get("authorization_url");
                OAuth2CallbackUrl = Get("callback_url", "http://127.0.0.1:8765/oauth/callback");
                OAuth2ClientId = Get("client_id");
                OAuth2ClientSecret = Get("client_secret");
                OAuth2Scope = Get("scope");
                OAuth2State = Get("state");
                OAuth2UsePkce = Flag("use_pkce", true);
                OAuth2Username = Get("username");
                OAuth2Password = Get("password");
                OAuth2CredentialsPlacement = Get("credentials_placement", "body");
                OAuth2RefreshTokenUrl = Get("refresh_token_url");
                OAuth2TokenSource = Get("token_source", "access_token");
                OAuth2TokenId = Get("token_id", "credentials");
                OAuth2AddTokenTo = Get("add_token_to", "headers");
                OAuth2HeaderPrefix = config.Parameters.TryGetValue("header_prefix", out var hp) ? hp : "Bearer";
                OAuth2AutoFetch = Flag("auto_fetch", true);
                OAuth2AutoRefresh = Flag("auto_refresh", false);
                LoadRows(OAuth2TokenParameters, Get("additional_token_params"));
                LoadRows(OAuth2RefreshParameters, Get("additional_refresh_params"));
                break;
            case DomainAuthType.AwsV4:
                AuthType = "awsv4";
                AwsAccessKeyId = Get("accessKeyId");
                AwsSecretAccessKey = Get("secretAccessKey");
                AwsRegion = Get("region");
                AwsService = Get("service");
                AwsSessionToken = Get("sessionToken");
                break;
            default:
                AuthType = "none";
                break;
        }
    }

    private static IEnumerable<AdditionalParamRow> ToRows(IEnumerable<OAuth2AdditionalParameter> rows) =>
        rows.Select(r => new AdditionalParamRow(r.Key, r.Value, r.SendIn, r.IsActive));

    private static void LoadRows(ObservableCollection<OAuth2AdditionalParameter> sink, string? json)
    {
        sink.Clear();
        foreach (var row in AuthParameters.DeserializeAdditionalParams(json))
        {
            sink.Add(new OAuth2AdditionalParameter
            {
                Key = row.Key,
                Value = row.Value,
                SendIn = row.SendIn,
                IsActive = row.Enabled,
            });
        }
        KvAutoAppend.EnsureTrailingBlank(sink, () => new OAuth2AdditionalParameter(), r => r.IsBlank);
    }

    // ---- OAuth2 panel commands ---------------------------------------------

    /// <summary>"Get Access Token" — runs the configured grant now and surfaces the result in
    /// the panel. Uses exactly the same code path a Send does, so what you see here is what
    /// goes on the wire (including for a collection- or folder-level config).</summary>
    [RelayCommand]
    public async Task GetOAuth2AccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (AuthType != "oauth2")
        {
            OAuth2StatusMessage = "Auth type is not OAuth 2.0.";
            return;
        }
        if (_oauth2 is null)
        {
            OAuth2StatusMessage = "Token acquisition is unavailable in this context.";
            return;
        }

        var config = BuildAuthConfig();
        if (config is null) return;

        OAuth2TokenResult token;
        try
        {
            token = await AuthPreparer
                .AcquireOAuth2Async(config, ResolvedVariablesSnapshot, _oauth2, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            OAuth2StatusMessage = $"Failed: {ex.Message}";
            return;
        }

        if (!token.IsSuccess || string.IsNullOrEmpty(token.AccessToken))
        {
            OAuth2StatusMessage = token.ErrorMessage ?? "OAuth2 token acquisition failed.";
            return;
        }

        ApplyTokenResult(token);
        OAuth2StatusMessage = token.FromCache ? "Fetched from cache." : "Fetched.";
    }

    /// <summary>Mirrors an acquired token into the panel preview. Also called by hosts after a
    /// Send performed the exchange, so the panel reflects what was actually used.</summary>
    public void ApplyTokenResult(OAuth2TokenResult token)
    {
        if (string.IsNullOrEmpty(token.AccessToken)) return;
        OAuth2LastAccessToken = token.AccessToken!;
        OAuth2TokenType = string.IsNullOrEmpty(token.TokenType) ? "Bearer" : token.TokenType!;
        OAuth2DecodedPayload = JwtDecoder.PrettyPrintPayload(token.AccessToken!);
    }

    /// <summary>Drops the cache slot for this configuration so the next fetch hits the wire.</summary>
    [RelayCommand]
    public void ClearOAuth2Cache()
    {
        _oauth2?.InvalidateCacheForTokenId(OAuth2TokenId);
        OAuth2LastAccessToken = string.Empty;
        OAuth2DecodedPayload = string.Empty;
        OAuth2StatusMessage = "Cache cleared.";
    }

    [RelayCommand]
    public void AddOAuth2TokenParameter() => OAuth2TokenParameters.Add(new OAuth2AdditionalParameter());

    [RelayCommand]
    public void AddOAuth2RefreshParameter() => OAuth2RefreshParameters.Add(new OAuth2AdditionalParameter());

    [RelayCommand]
    public void RemoveOAuth2TokenParameter(OAuth2AdditionalParameter? row)
    {
        if (row is not null) OAuth2TokenParameters.Remove(row);
    }

    [RelayCommand]
    public void RemoveOAuth2RefreshParameter(OAuth2AdditionalParameter? row)
    {
        if (row is not null) OAuth2RefreshParameters.Remove(row);
    }

    /// <summary>Eye-icon toggle for the Client Secret field.</summary>
    [RelayCommand]
    public void ToggleOAuth2SecretVisibility() => OAuth2IsClientSecretVisible = !OAuth2IsClientSecretVisible;
}
