using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using Vegha.Core.Interpolation;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using Vegha.Integrations.Wsdl;
using Microsoft.Extensions.Logging;
// Disambiguate VM's "AuthType" string property from Domain's AuthType enum.
using DomainAuthType = Vegha.Core.Domain.AuthType;

namespace Vegha.App.ViewModels;

public partial class RequestEditorViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> SupportedMethods = new[]
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"
    };

    public static readonly IReadOnlyList<string> BodyTypes = new[]
    {
        "none", "json", "text", "xml", "sparql",
        "multipart-form", "form-urlencoded", "file",
        "graphql",
    };

    /// <summary>Maps a BodyType to the AvaloniaEdit syntax-highlighting definition name.
    /// Returns null for types that have no built-in highlighter (text, sparql, form modes,
    /// no body). The body editor reads this when rendering the editor pane.</summary>
    public static string? SyntaxHighlightForBodyType(string bodyType) => bodyType switch
    {
        "json" => "Json",
        "xml"  => "XML",
        _      => null,
    };

    /// <summary>The set of body types that should render the raw text editor (with line numbers
    /// + variable highlighting). Form / file / no-body modes get a different surface.</summary>
    public static bool IsRawBodyType(string bodyType) =>
        bodyType is "json" or "xml" or "text" or "sparql";

    public static readonly IReadOnlyList<string> AuthTypes = new[]
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

    private readonly HttpExecutor _executor;
    private readonly OAuth2TokenAcquirer _oauth2;
    private readonly ILogger<RequestEditorViewModel> _logger;
    private readonly JintHost _scriptHost;
    private readonly Vegha.Core.History.HistoryStore? _historyStore;
    private readonly Vegha.Integrations.Secrets.SecretRegistry? _secretRegistry;

    /// <summary>Pre-request script body. Set by LoadFromRequestItem; not currently editable in UI.</summary>
    [ObservableProperty]
    private string _preRequestScript = string.Empty;

    /// <summary>Post-response script body — runs after the response, before tests.
    /// Bruno parity (<c>script:post-response { }</c>). Separate from <see cref="TestsScript"/>:
    /// post-response is for side-effects (var extraction, header inspection); tests is for assertions.</summary>
    [ObservableProperty]
    private string _postResponseScript = string.Empty;

    /// <summary>Tests block body (assertions via test()/expect()). Set by LoadFromRequestItem.</summary>
    [ObservableProperty]
    private string _testsScript = string.Empty;

    /// <summary>Per-request markdown docs. Renders alongside the request in the Docs tab.</summary>
    [ObservableProperty]
    private string _docs = string.Empty;

    /// <summary>Parent collection + folder chain — set by the host when this editor is bound to a tab.
    /// Drives header / auth / var / script inheritance at SendAsync time via RequestComposition.</summary>
    private Vegha.Core.Domain.Collection? _parentCollection;
    private IReadOnlyList<Vegha.Core.Domain.Folder> _parentFolderChain = Array.Empty<Vegha.Core.Domain.Folder>();

    /// <summary>Workspace-level vars + scripts merged underneath the collection layer.
    /// Pulled fresh from <see cref="WorkspaceContextProvider"/> on every Compose call so a
    /// workspace switch flows through without re-binding every open tab.</summary>
    public Func<Vegha.Core.Requests.RequestComposition.WorkspaceContext>? WorkspaceContextProvider { get; set; }

    /// <summary>Supplies the active workspace's id (its folder path) so a recorded history row is
    /// tagged with the workspace it was sent from. Pulled fresh on every Send so a workspace
    /// switch flows through without re-binding open tabs. Null → the row is recorded unscoped.</summary>
    public Func<string?>? HistoryWorkspaceIdProvider { get; set; }

    private Vegha.Core.Requests.RequestComposition.WorkspaceContext CurrentWorkspaceContext()
        => WorkspaceContextProvider?.Invoke()
           ?? Vegha.Core.Requests.RequestComposition.WorkspaceContext.Empty;

    public void SetParentContext(
        Vegha.Core.Domain.Collection? collection,
        IReadOnlyList<Vegha.Core.Domain.Folder>? folderChain)
    {
        _parentCollection = collection;
        _parentFolderChain = folderChain ?? Array.Empty<Vegha.Core.Domain.Folder>();
        RefreshInheritanceHints();
    }

    // ---- Inheritance hints (drives "Inherited from …" labels + Override buttons) ----
    [ObservableProperty] private string? _authInheritedFrom;
    [ObservableProperty] private string? _preRequestScriptInheritedFrom;
    [ObservableProperty] private string? _postResponseScriptInheritedFrom;
    [ObservableProperty] private string? _testsScriptInheritedFrom;

    public bool IsAuthInherited => !string.IsNullOrEmpty(AuthInheritedFrom);
    public bool IsPreRequestScriptInherited => !string.IsNullOrEmpty(PreRequestScriptInheritedFrom);
    public bool IsPostResponseScriptInherited => !string.IsNullOrEmpty(PostResponseScriptInheritedFrom);
    public bool IsTestsScriptInherited => !string.IsNullOrEmpty(TestsScriptInheritedFrom);

    partial void OnAuthInheritedFromChanged(string? value) => OnPropertyChanged(nameof(IsAuthInherited));
    partial void OnPreRequestScriptInheritedFromChanged(string? value) => OnPropertyChanged(nameof(IsPreRequestScriptInherited));
    partial void OnPostResponseScriptInheritedFromChanged(string? value) => OnPropertyChanged(nameof(IsPostResponseScriptInherited));
    partial void OnTestsScriptInheritedFromChanged(string? value) => OnPropertyChanged(nameof(IsTestsScriptInherited));

    /// <summary>Recomputes the inherited-from labels using the current parent context.
    /// Called on parent-context change and after Override actions.</summary>
    public void RefreshInheritanceHints()
    {
        var collection = _parentCollection ?? new Vegha.Core.Domain.Collection { Name = string.Empty };
        var thisRequest = SnapshotAsRequest();
        var (_, sources) = Vegha.Core.Requests.RequestComposition.ComposeWithSources(
            collection, _parentFolderChain, thisRequest, CurrentWorkspaceContext());
        AuthInheritedFrom = sources.Auth;
        PreRequestScriptInheritedFrom = sources.PreRequestScript;
        PostResponseScriptInheritedFrom = sources.PostResponseScript;
        TestsScriptInheritedFrom = sources.TestsScript;
    }

    /// <summary>Materializes inherited auth onto this request (Override action). Pulls the
    /// composed auth and copies it onto the editor's own auth fields, leaving the parent
    /// chain intact but breaking the inheritance link for this request.</summary>
    [RelayCommand]
    public void OverrideInheritedAuth()
    {
        if (!IsAuthInherited) return;
        var collection = _parentCollection ?? new Vegha.Core.Domain.Collection { Name = string.Empty };
        var composed = Vegha.Core.Requests.RequestComposition.Compose(
            collection, _parentFolderChain, SnapshotAsRequest(), CurrentWorkspaceContext());
        if (composed.Auth is null) return;
        ApplyAuthConfig(composed.Auth);
        RefreshInheritanceHints();
    }

    [RelayCommand]
    public void OverrideInheritedPreRequestScript()
    {
        if (!IsPreRequestScriptInherited) return;
        var collection = _parentCollection ?? new Vegha.Core.Domain.Collection { Name = string.Empty };
        var composed = Vegha.Core.Requests.RequestComposition.Compose(
            collection, _parentFolderChain, SnapshotAsRequest(), CurrentWorkspaceContext());
        PreRequestScript = composed.PreRequestScript ?? string.Empty;
        RefreshInheritanceHints();
    }

    [RelayCommand]
    public void OverrideInheritedPostResponseScript()
    {
        if (!IsPostResponseScriptInherited) return;
        var collection = _parentCollection ?? new Vegha.Core.Domain.Collection { Name = string.Empty };
        var composed = Vegha.Core.Requests.RequestComposition.Compose(
            collection, _parentFolderChain, SnapshotAsRequest(), CurrentWorkspaceContext());
        PostResponseScript = composed.PostResponseScript ?? string.Empty;
        RefreshInheritanceHints();
    }

    [RelayCommand]
    public void OverrideInheritedTestsScript()
    {
        if (!IsTestsScriptInherited) return;
        var collection = _parentCollection ?? new Vegha.Core.Domain.Collection { Name = string.Empty };
        var composed = Vegha.Core.Requests.RequestComposition.Compose(
            collection, _parentFolderChain, SnapshotAsRequest(), CurrentWorkspaceContext());
        TestsScript = composed.TestsScript ?? string.Empty;
        RefreshInheritanceHints();
    }

    private Vegha.Core.Domain.RequestItem SnapshotAsRequest() => new()
    {
        Name = SourcePath ?? "request",
        Method = Method,
        Url = Url,
        Headers = Headers
            .Where(h => h.IsActive && !string.IsNullOrEmpty(h.Name))
            .Select(h => new Vegha.Core.Domain.KvPair(h.Name, h.Value, h.IsActive))
            .ToList(),
        Auth = BuildAuthConfig(),
        PreRequestScript = PreRequestScript,
        PostResponseScript = PostResponseScript,
        Tests = TestsScript,
        Docs = Docs,
        PreRequestVars = Variables
            .Where(v => v.IsActive && !string.IsNullOrEmpty(v.Name))
            .Select(v => new Vegha.Core.Domain.KvPair(v.Name, v.Value, v.IsActive))
            .ToList(),
    };

    /// <summary>Snapshots the current editor state into a <see cref="Vegha.Core.Domain.RequestItem"/>
    /// and runs <see cref="Vegha.Core.Requests.RequestComposition.Compose"/> against the parent
    /// context (if any). For a draft tab without a parent, returns a composed view of just this
    /// editor's values — same as today's behavior.</summary>
    private Vegha.Core.Requests.RequestComposition.Composed ComposeWithInheritance()
    {
        var thisRequest = new Vegha.Core.Domain.RequestItem
        {
            Name = SourcePath ?? "request",
            Method = Method,
            Url = Url,
            Headers = Headers
                .Where(h => h.IsActive && !string.IsNullOrEmpty(h.Name))
                .Select(h => new Vegha.Core.Domain.KvPair(h.Name, h.Value, h.IsActive))
                .ToList(),
            Auth = BuildAuthConfig(),
            PreRequestScript = PreRequestScript,
            PostResponseScript = PostResponseScript,
            Tests = TestsScript,
            Docs = Docs,
            PreRequestVars = Variables
                .Where(v => v.IsActive && !string.IsNullOrEmpty(v.Name))
                .Select(v => new Vegha.Core.Domain.KvPair(v.Name, v.Value, v.IsActive))
                .ToList(),
        };

        var collection = _parentCollection ?? new Vegha.Core.Domain.Collection { Name = string.Empty };
        return Vegha.Core.Requests.RequestComposition.Compose(collection, _parentFolderChain, thisRequest, CurrentWorkspaceContext());
    }

    // ---- Per-request settings ----
    [ObservableProperty] private bool _settingFollowRedirects = true;
    [ObservableProperty] private bool _settingVerifySsl       = true;
    [ObservableProperty] private bool _settingEncodeUrl       = true;
    [ObservableProperty] private bool _settingSendCookies     = true;
    [ObservableProperty] private bool _settingSaveCookies     = true;
    [ObservableProperty] private bool _settingHttp2           = false;

    // ---- SOAP WS-Security / WS-Addressing (SOAP requests only) ----
    /// <summary>True when this request was loaded as SOAP (<c>meta.type: soap</c>) — drives
    /// the SOAP tab's visibility in the request editor.</summary>
    public bool IsSoapRequest =>
        string.Equals(_loadedItem?.MetaType, "soap", StringComparison.OrdinalIgnoreCase);

    /// <summary>Options for the WSS UsernameToken password-type picker.</summary>
    public IReadOnlyList<string> SoapPasswordTypes { get; } = new[] { "text", "digest" };

    [ObservableProperty] private bool _soapTimestampEnabled;
    [ObservableProperty] private int _soapTimestampTtl = 60;
    [ObservableProperty] private bool _soapUsernameTokenEnabled;
    [ObservableProperty] private string _soapWssUsername = string.Empty;
    [ObservableProperty] private string _soapWssPassword = string.Empty;
    [ObservableProperty] private string _soapWssPasswordType = "text";
    [ObservableProperty] private bool _soapWssAddNonce = true;
    [ObservableProperty] private bool _soapWssAddCreated = true;
    [ObservableProperty] private bool _soapAddressingEnabled;
    [ObservableProperty] private string _soapWsaAction = string.Empty;
    [ObservableProperty] private string _soapWsaTo = string.Empty;
    [ObservableProperty] private string _soapWsaReplyTo = string.Empty;
    [ObservableProperty] private string _soapWsaMessageId = string.Empty;
    [ObservableProperty] private bool _soapWsaAutoMessageId = true;

    partial void OnSoapTimestampEnabledChanged(bool value)      { if (!_loading) IsDirty = true; }
    partial void OnSoapTimestampTtlChanged(int value)           { if (!_loading) IsDirty = true; }
    partial void OnSoapUsernameTokenEnabledChanged(bool value)  { if (!_loading) IsDirty = true; }
    partial void OnSoapWssUsernameChanged(string value)         { if (!_loading) IsDirty = true; }
    partial void OnSoapWssPasswordChanged(string value)         { if (!_loading) IsDirty = true; }
    partial void OnSoapWssPasswordTypeChanged(string value)     { if (!_loading) IsDirty = true; }
    partial void OnSoapWssAddNonceChanged(bool value)           { if (!_loading) IsDirty = true; }
    partial void OnSoapWssAddCreatedChanged(bool value)         { if (!_loading) IsDirty = true; }
    partial void OnSoapAddressingEnabledChanged(bool value)     { if (!_loading) IsDirty = true; }
    partial void OnSoapWsaActionChanged(string value)           { if (!_loading) IsDirty = true; }
    partial void OnSoapWsaToChanged(string value)               { if (!_loading) IsDirty = true; }
    partial void OnSoapWsaReplyToChanged(string value)          { if (!_loading) IsDirty = true; }
    partial void OnSoapWsaMessageIdChanged(string value)        { if (!_loading) IsDirty = true; }
    partial void OnSoapWsaAutoMessageIdChanged(bool value)      { if (!_loading) IsDirty = true; }

    /// <summary>Builds a <see cref="SoapConfig"/> from the SOAP tab fields. Returns null when
    /// no section is enabled — keeps non-SOAP requests and unconfigured SOAP requests clean.</summary>
    private SoapConfig? BuildSoapConfig()
    {
        var timestamp = SoapTimestampEnabled
            ? new WssTimestampConfig { TimeToLiveSeconds = Math.Max(1, SoapTimestampTtl) }
            : null;

        var usernameToken = SoapUsernameTokenEnabled
            ? new WssUsernameTokenConfig
            {
                Username = SoapWssUsername,
                Password = SoapWssPassword,
                PasswordType = string.Equals(SoapWssPasswordType, "digest", StringComparison.OrdinalIgnoreCase)
                    ? WssPasswordType.Digest
                    : WssPasswordType.Text,
                AddNonce = SoapWssAddNonce,
                AddCreated = SoapWssAddCreated,
            }
            : null;

        var addressing = SoapAddressingEnabled
            ? new WsAddressingConfig
            {
                Action = NullIfBlank(SoapWsaAction),
                To = NullIfBlank(SoapWsaTo),
                ReplyTo = NullIfBlank(SoapWsaReplyTo),
                MessageId = NullIfBlank(SoapWsaMessageId),
                AutoMessageId = SoapWsaAutoMessageId,
            }
            : null;

        if (timestamp is null && usernameToken is null && addressing is null) return null;
        return new SoapConfig { Timestamp = timestamp, UsernameToken = usernameToken, Addressing = addressing };

        static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public ObservableCollection<TestResultRow> TestResults { get; } = new();

    /// <summary>True when there is at least one test result — drives the summary header's visibility.</summary>
    public bool HasTestResults => TestResults.Count > 0;
    /// <summary>Number of passed tests in <see cref="TestResults"/> (for the Bruno-style summary).</summary>
    public int TestsPassedCount => TestResults.Count(t => t.Passed);
    /// <summary>Number of failed tests in <see cref="TestResults"/> (for the Bruno-style summary).</summary>
    public int TestsFailedCount => TestResults.Count(t => !t.Passed);

    // ---- Run & inspect (run a script in isolation against the last response) ----

    /// <summary>Console output captured from the most recent "Run &amp; inspect".</summary>
    public ObservableCollection<ConsoleLineRow> InspectConsole { get; } = new();

    /// <summary>Variables produced by the most recent run, grouped by scope.</summary>
    public ObservableCollection<InspectVarRow> InspectVariables { get; } = new();

    /// <summary>Test outcomes from the most recent Tests run.</summary>
    public ObservableCollection<TestResultRow> InspectTests { get; } = new();

    /// <summary>True while the run-output panel is visible.</summary>
    [ObservableProperty]
    private bool _isInspectPanelOpen;

    /// <summary>Heading for the run-output panel (e.g. "Pre-request run").</summary>
    [ObservableProperty]
    private string _inspectTitle = string.Empty;

    /// <summary>Status / error / hint line for the run-output panel (e.g. script error, "send first").</summary>
    [ObservableProperty]
    private string? _inspectMessage;

    [ObservableProperty] private bool _inspectHasConsole;
    [ObservableProperty] private bool _inspectHasVariables;
    [ObservableProperty] private bool _inspectHasTests;

    [ObservableProperty]
    private string _method = "GET";

    [ObservableProperty]
    private string _url = string.Empty;

    /// <summary>Path on disk this request was loaded from. Null for unsaved/new requests.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string? _sourcePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSendCommand))]
    private bool _isSending;

    /// <summary>Seconds elapsed since the in-flight request began. Drives the live timer
    /// in the response panel's busy overlay; updated ~10× per second while
    /// <see cref="IsSending"/> is true and pinned to 0 otherwise.</summary>
    [ObservableProperty]
    private double _sendingElapsedSeconds;

    /// <summary>"0.6s" style formatted view of <see cref="SendingElapsedSeconds"/>. Static
    /// computed property so the overlay's XAML stays a single binding without converters.</summary>
    public string SendingElapsedDisplay => $"{SendingElapsedSeconds:F1}s";

    partial void OnSendingElapsedSecondsChanged(double value) => OnPropertyChanged(nameof(SendingElapsedDisplay));

    /// <summary>CTS for the in-flight request. Linked to the command's own token so external
    /// cancellation (window close, command re-execution) still flows through. Replaced on
    /// every SendAsync, cancelled by <see cref="CancelSend"/>.</summary>
    private CancellationTokenSource? _sendCts;

    // Default to Body (index 4 — Params/Auth/Headers/Vars/Body/...) — that's where the
    // user usually starts editing a new request. Without this the editor opens on Params,
    // which is empty for most cases.
    [ObservableProperty]
    private int _requestTabIndex = 4;

    /// <summary>True when the Body sub-tab is the active sub-tab. Index 4 in the
    /// Params/Authorization/Headers/Vars/Body/... order. Drives the right-aligned
    /// body-type picker + Prettify button on the sub-tab header row.</summary>
    public bool IsBodyTabSelected => RequestTabIndex == 4;

    partial void OnRequestTabIndexChanged(int value) => OnPropertyChanged(nameof(IsBodyTabSelected));

    // ---- Tab "has data" indicators ----
    // Each sub-tab's header shows a small superscript dot when the tab carries data. These
    // booleans drive the dot's IsVisible. Avoids inline converter chains in the XAML.
    /// <summary>True when the Body tab is anything other than "none" — drives the body
    /// header's data dot.</summary>
    public bool BodyHasData => !string.Equals(BodyType, "none", StringComparison.OrdinalIgnoreCase);
    /// <summary>True when an auth type is configured (anything but "none" / "inherit").</summary>
    public bool AuthHasData =>
        !string.IsNullOrEmpty(AuthType) &&
        !string.Equals(AuthType, "none", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(AuthType, "inherit", StringComparison.OrdinalIgnoreCase);
    public bool PreRequestScriptHasData => !string.IsNullOrWhiteSpace(PreRequestScript);
    public bool PostResponseScriptHasData => !string.IsNullOrWhiteSpace(PostResponseScript);
    public bool TestsScriptHasData => !string.IsNullOrWhiteSpace(TestsScript);
    public bool DocsHasData => !string.IsNullOrWhiteSpace(Docs);

    /// <summary>Combined count of pre-request + post-response variables — drives the Vars
    /// tab's single superscript count badge so the user sees one number at a glance.
    /// Excludes the auto-appended trailing ghost rows (they're chrome, not data).</summary>
    public int VarsTotalCount => Variables.Count(v => !v.IsBlank) + PostResponseVariables.Count(v => !v.IsBlank);

    /// <summary>Non-blank row counts for the Params / Headers tab badges. Raw
    /// <c>Params.Count</c> would always be ≥1 because of the trailing ghost row.</summary>
    public int ParamsCount => Params.Count(p => !p.IsBlank);
    public int HeadersCount => Headers.Count(h => !h.IsBlank);

    [ObservableProperty]
    private string _bodyType = "none";

    [ObservableProperty]
    private string _bodyContent = string.Empty;

    // ----- Form-URL-Encoded body — table rows -----
    /// <summary>Key/value pairs sent as <c>application/x-www-form-urlencoded</c> when
    /// <see cref="BodyType"/> = "form-urlencoded". Round-trips through <c>BodyConfig.FormData</c>.</summary>
    public ObservableCollection<KvEntry> FormUrlEncodedItems { get; } = new();

    // ----- Multipart-form body — rich rows -----
    /// <summary>Field rows for the multipart-form body editor. Each row is either a text
    /// part or a file part (<see cref="MultipartFormRow.Kind"/>). Round-trips through
    /// <c>BodyConfig.MultipartItems</c>.</summary>
    public ObservableCollection<MultipartFormRow> MultipartItems { get; } = new();

    public static readonly IReadOnlyList<string> MultipartKinds = new[] { "text", "file" };
    public IReadOnlyList<string> AvailableMultipartKinds => MultipartKinds;

    // ----- File / Binary body — single file -----
    /// <summary>Absolute path of the file streamed as the request body when
    /// <see cref="BodyType"/> = "file". Picked through the body editor's file dialog.</summary>
    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary>Optional Content-Type override for the file body. Empty falls back to the
    /// MIME guessed from the file extension.</summary>
    [ObservableProperty]
    private string _fileContentType = string.Empty;

    // GraphQL — separate from BodyContent so the UI can edit query and variables independently.
    [ObservableProperty]
    private string _graphQLQuery = string.Empty;

    [ObservableProperty]
    private string _graphQLVariables = string.Empty;

    /// <summary>Cached schema browser text (built from a successful introspection). Wired
    /// into the GraphQL body editor's schema panel.</summary>
    [ObservableProperty]
    private string _graphQLSchemaSummary = string.Empty;

    [ObservableProperty]
    private bool _graphQLSchemaLoaded;

    // ---- Auth ----
    [ObservableProperty]
    private string _authType = "none";

    [ObservableProperty]
    private string _bearerToken = string.Empty;

    [ObservableProperty]
    private string _basicUsername = string.Empty;

    [ObservableProperty]
    private string _basicPassword = string.Empty;

    [ObservableProperty]
    private string _digestUsername = string.Empty;

    [ObservableProperty]
    private string _digestPassword = string.Empty;

    [ObservableProperty]
    private string _ntlmUsername = string.Empty;

    [ObservableProperty]
    private string _ntlmPassword = string.Empty;

    [ObservableProperty]
    private string _ntlmDomain = string.Empty;

    // OAuth1 (RFC 5849)
    [ObservableProperty] private string _oAuth1ConsumerKey = string.Empty;
    [ObservableProperty] private string _oAuth1ConsumerSecret = string.Empty;
    [ObservableProperty] private string _oAuth1Token = string.Empty;
    [ObservableProperty] private string _oAuth1TokenSecret = string.Empty;
    [ObservableProperty] private string _oAuth1SignatureMethod = "HMAC-SHA1";
    [ObservableProperty] private string _oAuth1Realm = string.Empty;

    public IReadOnlyList<string> AvailableOAuth1SignatureMethods => OAuth1SignatureMethods;

    // WSSE UsernameToken (SOAP WS-Security)
    [ObservableProperty] private string _wsseUsername = string.Empty;
    [ObservableProperty] private string _wssePassword = string.Empty;

    // mTLS client certificate (per-request)
    [ObservableProperty] private string _mtlsCertPath = string.Empty;
    [ObservableProperty] private string _mtlsCertPassword = string.Empty;

    [ObservableProperty]
    private string _apiKeyName = "X-API-Key";

    [ObservableProperty]
    private string _apiKeyValue = string.Empty;

    [ObservableProperty]
    private string _apiKeyPlacement = "header";

    // OAuth2 (client_credentials)
    [ObservableProperty]
    private string _oAuth2GrantType = "client_credentials";

    [ObservableProperty]
    private string _oAuth2TokenUrl = string.Empty;

    [ObservableProperty]
    private string _oAuth2ClientId = string.Empty;

    [ObservableProperty]
    private string _oAuth2ClientSecret = string.Empty;

    [ObservableProperty]
    private string _oAuth2Scope = string.Empty;

    [ObservableProperty]
    private string _oAuth2CredentialsPlacement = "body";

    // OAuth2 password grant
    [ObservableProperty]
    private string _oAuth2Username = string.Empty;

    [ObservableProperty]
    private string _oAuth2Password = string.Empty;

    // OAuth2 authorization_code (with PKCE)
    [ObservableProperty]
    private string _oAuth2AuthorizationUrl = string.Empty;

    [ObservableProperty]
    private string _oAuth2CallbackUrl = "http://127.0.0.1:8765/oauth/callback";

    [ObservableProperty]
    private string _oAuth2State = string.Empty;

    [ObservableProperty]
    private bool _oAuth2UsePkce = true;

    // ----- OAuth2: Bruno-parity additions -----

    /// <summary>Optional second token endpoint used for refresh_token grant calls. Empty
    /// falls back to <see cref="OAuth2TokenUrl"/>. Matches Bruno's "Refresh Token URL".</summary>
    [ObservableProperty]
    private string _oAuth2RefreshTokenUrl = string.Empty;

    /// <summary>Which field of the token response to use as the request's bearer.
    /// "access_token" (default) / "id_token" / "refresh_token". Aligns with Bruno's
    /// "Token Source" dropdown — useful for IdPs that issue both access + id tokens
    /// and you need the id_token on downstream requests.</summary>
    [ObservableProperty]
    private string _oAuth2TokenSource = "access_token";

    /// <summary>Stable label used in the cache key so multiple OAuth2 clients sharing the
    /// same (tokenUrl, clientId, scope) tuple can have independent cache slots. Defaults
    /// to "credentials" matching Bruno. Set to e.g. "user-a" / "user-b" to isolate.</summary>
    [ObservableProperty]
    private string _oAuth2TokenId = "credentials";

    /// <summary>Where the acquired token is attached on the outgoing request:
    /// "headers" (default) / "queryparams" / "body".</summary>
    [ObservableProperty]
    private string _oAuth2AddTokenTo = "headers";

    /// <summary>String prepended to the token when injecting into Authorization header.
    /// Defaults to "Bearer". Some IdPs require "JWT", "Token", or empty.</summary>
    [ObservableProperty]
    private string _oAuth2HeaderPrefix = "Bearer";

    /// <summary>When true, Send automatically acquires a token if one isn't already cached.
    /// Mirrors Bruno's "Automatically fetch token if not found".</summary>
    [ObservableProperty]
    private bool _oAuth2AutoFetch = true;

    /// <summary>When true and a refresh_token is available, the acquirer refreshes the
    /// access token transparently before it expires. Mirrors Bruno's "Auto refresh token
    /// (with refresh URL)".</summary>
    [ObservableProperty]
    private bool _oAuth2AutoRefresh;

    /// <summary>UI-only — toggles the client-secret eye icon between * and plaintext.
    /// Not persisted.</summary>
    [ObservableProperty]
    private bool _oAuth2IsClientSecretVisible;

    /// <summary>The most recently acquired access token, surfaced in the "Access Token"
    /// section of the auth panel. Updated when the user clicks Get Access Token or Send.
    /// Not persisted — it's a live runtime value.</summary>
    [ObservableProperty]
    private string _oAuth2LastAccessToken = string.Empty;

    /// <summary>Pretty-printed JWT payload of <see cref="OAuth2LastAccessToken"/> when it
    /// looks like a JWT. Empty otherwise. Not persisted.</summary>
    [ObservableProperty]
    private string _oAuth2DecodedPayload = string.Empty;

    /// <summary>Token type from the most recent token response (typically "Bearer").
    /// Surfaced in the Access Token preview. Not persisted.</summary>
    [ObservableProperty]
    private string _oAuth2TokenType = "Bearer";

    /// <summary>Status text shown next to the Get Access Token button — last fetch outcome
    /// ("Fetched from cache", error messages, etc.). Not persisted.</summary>
    [ObservableProperty]
    private string _oAuth2StatusMessage = string.Empty;

    /// <summary>Free-form key/value/where-to-send rows added to the token request. Bruno
    /// surfaces these under "Additional Parameters → Token".</summary>
    public ObservableCollection<OAuth2AdditionalParameter> OAuth2TokenParameters { get; } = new();

    /// <summary>Same shape, applied only to refresh_token grant requests.</summary>
    public ObservableCollection<OAuth2AdditionalParameter> OAuth2RefreshParameters { get; } = new();

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

    public IReadOnlyList<string> AvailableOAuth2TokenSources => OAuth2TokenSources;
    public IReadOnlyList<string> AvailableOAuth2AddTokenTo => OAuth2AddTokenToOptions;
    public IReadOnlyList<string> AvailableOAuth2ParamSendIn => OAuth2AdditionalParamSendIn;

    // AWS SigV4
    [ObservableProperty] private string _awsAccessKeyId     = string.Empty;
    [ObservableProperty] private string _awsSecretAccessKey = string.Empty;
    [ObservableProperty] private string _awsRegion          = string.Empty;
    [ObservableProperty] private string _awsService         = string.Empty;
    [ObservableProperty] private string _awsSessionToken    = string.Empty;

    public IReadOnlyList<string> AvailableAuthTypes => AuthTypes;
    public IReadOnlyList<string> AvailableApiKeyPlacements => ApiKeyPlacements;
    public IReadOnlyList<string> AvailableOAuth2GrantTypes => OAuth2GrantTypes;
    public IReadOnlyList<string> AvailableOAuth2CredentialPlacements => OAuth2CredentialPlacements;

    [ObservableProperty]
    private int _responseStatusCode;

    [ObservableProperty]
    private string _responseStatusText = string.Empty;

    [ObservableProperty]
    private string _responseBody = string.Empty;

    [ObservableProperty]
    private long _responseElapsedMilliseconds;

    [ObservableProperty]
    private double _timingDnsMs;

    [ObservableProperty]
    private double _timingConnectMs;

    [ObservableProperty]
    private double _timingTlsMs;

    [ObservableProperty]
    private double _timingTtfbMs;

    [ObservableProperty]
    private double _timingContentMs;

    [ObservableProperty]
    private double _timingTotalMs;

    public ObservableCollection<TimelinePhase> TimelinePhases { get; } = new();

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasResponse;

    [ObservableProperty]
    private string _rawResponseText = string.Empty;

    /// <summary>HTTP-style text of the outgoing request (request line + headers + body)
    /// captured at send time. Visible on the "Sent" subtab — shows exactly what crossed
    /// the wire so the user can diff against a working curl invocation.</summary>
    [ObservableProperty]
    private string _sentRequestText = string.Empty;

    [ObservableProperty]
    private int _responseTabIndex;

    /// <summary>Whether the response Body editor soft-wraps long lines. Defaults to true:
    /// JSON/XML/text reads better wrapped, and wrap avoids the scrollbar-drift issue where
    /// AvaloniaEdit can render the V-scrollbar at the rendered text's right edge instead
    /// of the viewport edge when horizontal scrolling is enabled. Bound to the "Wrap" toggle
    /// in the Body sub-tab toolbar.</summary>
    [ObservableProperty]
    private bool _responseWordWrap = true;

    public ObservableCollection<KvEntry> Params { get; } = new();
    public ObservableCollection<KvEntry> Headers { get; } = new();
    /// <summary>Request-scoped variables applied BEFORE the request is sent — they merge
    /// into the {{var}} interpolation pass. Bound to the Vars tab's "Pre Request" section.
    /// Round-trips through <see cref="RequestItem.PreRequestVars"/>.</summary>
    public ObservableCollection<KvEntry> Variables { get; } = new();
    /// <summary>Variables assigned AFTER the response — Value strings can be raw or
    /// JavaScript expressions evaluated against res/req/bru (Bruno parity). Bound to the
    /// Vars tab's "Post Response" section. Round-trips through
    /// <see cref="RequestItem.PostResponseVars"/>.</summary>
    public ObservableCollection<KvEntry> PostResponseVariables { get; } = new();
    public ObservableCollection<HeaderRow> ResponseHeaders { get; } = new();
    public ObservableCollection<ResponseCookieRow> ResponseCookies { get; } = new();

    /// <summary>Raw bytes of the response (for image/PDF previews). Mirrors
    /// <see cref="ResponseBody"/>, which is the UTF-8 decoded text view.</summary>
    [ObservableProperty]
    private byte[] _responseBodyBytes = Array.Empty<byte>();

    /// <summary>Content-Type from the response (lower-cased), used by the body viewer
    /// to pick image / PDF / text rendering.</summary>
    [ObservableProperty]
    private string _responseContentType = string.Empty;

    public bool ResponseIsImage => ResponseContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public bool ResponseIsPdf => ResponseContentType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase);
    public bool ResponseIsTextual => !ResponseIsImage && !ResponseIsPdf;
    public bool ResponseIsJson => ResponseContentType.Contains("json", StringComparison.OrdinalIgnoreCase);
    public bool ResponseIsXml =>
        ResponseContentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
        ResponseContentType.Contains("soap", StringComparison.OrdinalIgnoreCase);

    partial void OnResponseContentTypeChanged(string value)
    {
        OnPropertyChanged(nameof(ResponseIsImage));
        OnPropertyChanged(nameof(ResponseIsPdf));
        OnPropertyChanged(nameof(ResponseIsTextual));
        OnPropertyChanged(nameof(ResponseIsJson));
        OnPropertyChanged(nameof(ResponseIsXml));
        OnPropertyChanged(nameof(ResponseSyntaxHighlightingName));
        OnPropertyChanged(nameof(ResponseCanvasSyntaxKind));
        OnPropertyChanged(nameof(UseRichResponseViewer));
        OnPropertyChanged(nameof(UseLargeResponseViewer));
        RecomputeDisplayedBody();
    }

    /// <summary>JSONPath expression filter applied to the body when the response is JSON.
    /// Empty = show full body. Result is exposed via <see cref="DisplayedBody"/>.</summary>
    [ObservableProperty]
    private string _jsonPathFilter = string.Empty;

    [ObservableProperty]
    private bool _xmlPrettyPrint;

    /// <summary>The set of formats the response viewer offers in the format dropdown.
    /// "auto" defers to the response Content-Type; "raw" is the unformatted bytes.</summary>
    public static readonly IReadOnlyList<string> ResponseDisplayFormats = new[]
    {
        "Auto", "JSON", "XML", "HTML", "Text", "Raw",
    };
    public IReadOnlyList<string> AvailableResponseFormats => ResponseDisplayFormats;

    /// <summary>User-picked response display format. "Auto" applies a formatter inferred from
    /// the response Content-Type; "Raw" shows bytes verbatim with no transformation.</summary>
    [ObservableProperty]
    private string _responseFormat = "Auto";

    /// <summary>Body-size cutoff (chars) above which the response viewer drops every
    /// per-character UI feature that scales poorly with document size — pretty-printing
    /// is skipped, JSONPath filtering is skipped, and AvaloniaEdit syntax highlighting is
    /// disabled. Without this, a multi-MB body locks the UI for several seconds on the
    /// first click because the JSON colorizer + word-wrap recompute run across the whole
    /// document. 1 MB is conservative — the user can still inspect the raw text.</summary>
    public const int LargeBodyCharThreshold = 1_000_000;

    /// <summary>True when the response body crosses <see cref="LargeBodyCharThreshold"/>
    /// — drives the "skip every expensive transform" branches below and is exposed so
    /// the response panel can show a hint that pretty-printing was skipped.</summary>
    public bool IsResponseTooLargeToPrettify =>
        (ResponseBody?.Length ?? 0) > LargeBodyCharThreshold;

    /// <summary>True when the rich AvaloniaEdit-based viewer should render the body.
    /// False for image / PDF responses (rendered separately) and for huge bodies — those
    /// route to <see cref="UseLargeResponseViewer"/> instead.</summary>
    public bool UseRichResponseViewer =>
        ResponseIsTextual && !IsResponseTooLargeToPrettify;

    /// <summary>True when the virtualized list-of-lines viewer should render the body.
    /// Triggers for textual responses past <see cref="LargeBodyCharThreshold"/>, where
    /// AvaloniaEdit's per-visual-line measurement locks the UI on every click.</summary>
    public bool UseLargeResponseViewer =>
        ResponseIsTextual && IsResponseTooLargeToPrettify;

    /// <summary>AvaloniaEdit syntax-highlighting name for the response body view, derived from
    /// <see cref="ResponseFormat"/> + (in Auto mode) the response Content-Type. Returns null
    /// for bodies above <see cref="LargeBodyCharThreshold"/> — AvaloniaEdit's colorizer runs
    /// line-by-line on every redraw and was the dominant click-time hang on multi-MB responses.</summary>
    public string? ResponseSyntaxHighlightingName
    {
        get
        {
            if (IsResponseTooLargeToPrettify) return null;
            var fmt = ResolveEffectiveResponseFormat();
            return fmt switch
            {
                "JSON" => "Json",
                "XML"  => "XML",
                "HTML" => "HTML",
                _      => null,
            };
        }
    }

    /// <summary>Canvas-viewer syntax token: "json" / "xml" / "none". The canvas-based
    /// large-body viewer reads this to pick its per-line tokenizer (it has its own
    /// JSON + XML highlighters, decoupled from AvaloniaEdit's).</summary>
    public string ResponseCanvasSyntaxKind
    {
        get
        {
            var fmt = ResolveEffectiveResponseFormat();
            return fmt switch
            {
                "JSON" => "json",
                "XML"  => "xml",
                _      => "none",
            };
        }
    }

    private string ResolveEffectiveResponseFormat()
    {
        if (!string.Equals(ResponseFormat, "Auto", StringComparison.OrdinalIgnoreCase))
            return ResponseFormat;
        if (ResponseIsJson) return "JSON";
        if (ResponseIsXml) return "XML";
        if (ResponseContentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return "HTML";
        return "Text";
    }

    /// <summary>The body text shown in the textual Body view, after applying the chosen
    /// <see cref="ResponseFormat"/> and any JSONPath filter. Backed by a cached field
    /// (<see cref="_displayedBodyCache"/>) populated by <see cref="RecomputeDisplayedBody"/>
    /// so the getter is O(1) — without caching the bound editor re-prettified the body on
    /// every binding evaluation, locking the UI on every click for multi-MB responses.</summary>
    public string DisplayedBody => _displayedBodyCache;
    private string _displayedBodyCache = string.Empty;

    /// <summary>Cancels in-flight background prettify so a freshly arrived response
    /// supersedes any pending JSON/XML formatting from the previous response.</summary>
    private CancellationTokenSource? _prettifyCts;

    /// <summary>True while a background pretty-print pass is running for a large body —
    /// the response toolbar shows a "Formatting…" hint so the user knows the verbatim
    /// view they're seeing will swap to a formatted version shortly.</summary>
    [ObservableProperty]
    private bool _isPrettifying;

    /// <summary>Recomputes <see cref="DisplayedBody"/> from the current
    /// (ResponseBody, ResponseFormat, JsonPathFilter, ContentType) tuple and fires
    /// PropertyChanged. Above <see cref="LargeBodyCharThreshold"/> chars the body is
    /// first published verbatim — so the viewer comes up immediately — then a background
    /// task pretty-prints JSON/XML (or evaluates a JSONPath filter and prettifies the
    /// result) and swaps in the formatted version when ready.</summary>
    private void RecomputeDisplayedBody()
    {
        // Cancel any background prettify / filter from a previous response.
        _prettifyCts?.Cancel();
        _prettifyCts = null;
        IsPrettifying = false;

        var body = ResponseBody;
        string next;
        var bgKind = LargeBodyBgKind.None;

        if (string.IsNullOrEmpty(body))
        {
            next = string.Empty;
        }
        else if (string.Equals(ResponseFormat, "Raw", StringComparison.OrdinalIgnoreCase))
        {
            next = body;
        }
        else if (IsResponseTooLargeToPrettify)
        {
            // Publish raw immediately so the viewer renders without delay, then upgrade
            // to formatted output (or filtered output) once the background pass finishes.
            next = body;
            var fmt = ResolveEffectiveResponseFormat();
            if (fmt == "JSON" && !string.IsNullOrWhiteSpace(JsonPathFilter))
                bgKind = LargeBodyBgKind.FilterPrettify;
            else if (fmt is "JSON" or "XML")
                bgKind = LargeBodyBgKind.Prettify;
        }
        else if (ResponseIsJson && !string.IsNullOrWhiteSpace(JsonPathFilter))
        {
            var filtered = TryApplyJsonPath(body, JsonPathFilter);
            next = filtered is not null ? PrettyByFormat(filtered) : PrettyByFormat(body);
        }
        else
        {
            next = PrettyByFormat(body);
        }

        if (!ReferenceEquals(_displayedBodyCache, next))
        {
            _displayedBodyCache = next;
            OnPropertyChanged(nameof(DisplayedBody));
        }

        switch (bgKind)
        {
            case LargeBodyBgKind.Prettify:
                StartBackgroundPrettify(body, ResolveEffectiveResponseFormat());
                break;
            case LargeBodyBgKind.FilterPrettify:
                StartBackgroundJsonPathFilter(body, JsonPathFilter);
                break;
        }
    }

    private enum LargeBodyBgKind { None, Prettify, FilterPrettify }

    /// <summary>Kicks off a background <see cref="Task.Run"/> that pretty-prints
    /// <paramref name="body"/> for the given <paramref name="format"/>, then marshals
    /// the formatted string back to the UI sync context and republishes
    /// <see cref="DisplayedBody"/>. Token-scoped so a newer response cancels the older
    /// pass before it overwrites the cache.</summary>
    private void StartBackgroundPrettify(string body, string format)
    {
        _prettifyCts = new CancellationTokenSource();
        var token = _prettifyCts.Token;
        var uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        IsPrettifying = true;

        Task.Run(() => PrettyOffThread(body, format, token), token)
            .ContinueWith(t =>
            {
                // Drop silently if a newer response superseded us mid-flight.
                if (token.IsCancellationRequested) return;
                if (!t.IsCompletedSuccessfully) { IsPrettifying = false; return; }
                var pretty = t.Result;
                if (pretty is null) { IsPrettifying = false; return; }
                if (!ReferenceEquals(_displayedBodyCache, pretty))
                {
                    _displayedBodyCache = pretty;
                    OnPropertyChanged(nameof(DisplayedBody));
                }
                IsPrettifying = false;
            }, uiScheduler);
    }

    /// <summary>Background JSONPath evaluation — parses <paramref name="body"/> into a
    /// <c>JsonNode</c>, runs <paramref name="path"/>, prettifies the result, marshals
    /// to UI thread, publishes via <see cref="DisplayedBody"/>. Same cancellation +
    /// IsPrettifying signaling as <see cref="StartBackgroundPrettify"/>.</summary>
    private void StartBackgroundJsonPathFilter(string body, string path)
    {
        _prettifyCts = new CancellationTokenSource();
        var token = _prettifyCts.Token;
        var uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        IsPrettifying = true;

        Task.Run(() => TryApplyJsonPath(body, path), token)
            .ContinueWith(t =>
            {
                if (token.IsCancellationRequested) return;
                if (!t.IsCompletedSuccessfully) { IsPrettifying = false; return; }
                var filtered = t.Result;
                // TryApplyJsonPath already returns a pretty-printed JSON string (or
                // "// no matches" / "// JSONPath error: …"); use it as-is.
                if (filtered is null) { IsPrettifying = false; return; }
                if (!ReferenceEquals(_displayedBodyCache, filtered))
                {
                    _displayedBodyCache = filtered;
                    OnPropertyChanged(nameof(DisplayedBody));
                }
                IsPrettifying = false;
            }, uiScheduler);
    }

    private static string? PrettyOffThread(string body, string format, CancellationToken token)
    {
        try
        {
            switch (format)
            {
                case "JSON":
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    token.ThrowIfCancellationRequested();
                    return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                }
                case "XML":
                {
                    var pretty = TryPrettyPrintXml(body);
                    token.ThrowIfCancellationRequested();
                    return pretty;
                }
                default:
                    return null;
            }
        }
        catch
        {
            // Malformed payload — keep the raw text in place rather than crashing the UI.
            return null;
        }
    }

    private string PrettyByFormat(string text)
    {
        var fmt = ResolveEffectiveResponseFormat();
        try
        {
            switch (fmt)
            {
                case "JSON":
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(text);
                    return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                }
                case "XML":
                {
                    var pretty = TryPrettyPrintXml(text);
                    return pretty ?? text;
                }
                // HTML / Text / unknown: identity. The XmlPrettyPrint toggle is honored as a
                // back-compat hint when the user has set it explicitly.
                default:
                    if (XmlPrettyPrint && ResponseIsXml)
                        return TryPrettyPrintXml(text) ?? text;
                    return text;
            }
        }
        catch
        {
            // Ill-formed response (e.g. JSON parse failure on a non-JSON body) — fall back
            // to the original text rather than dropping content.
            return text;
        }
    }

    partial void OnResponseBodyChanged(string value)
    {
        // Body size flips IsResponseTooLargeToPrettify which gates syntax highlighting,
        // the prettify path, and which viewer renders the response — refresh them all
        // alongside the cached displayed body so the response panel re-routes correctly.
        OnPropertyChanged(nameof(IsResponseTooLargeToPrettify));
        OnPropertyChanged(nameof(ResponseSyntaxHighlightingName));
        OnPropertyChanged(nameof(UseRichResponseViewer));
        OnPropertyChanged(nameof(UseLargeResponseViewer));
        RecomputeDisplayedBody();
    }
    partial void OnJsonPathFilterChanged(string value)
    {
        // Small bodies: re-evaluate inline (cheap, immediate feedback while typing).
        if (!IsResponseTooLargeToPrettify)
        {
            RecomputeDisplayedBody();
            return;
        }

        // Large bodies: debounce 400 ms so each keystroke doesn't kick off a full
        // JsonNode.Parse of the multi-MB body. The previous CTS cancels its Task.Delay
        // before the recompute fires.
        _filterDebounceCts?.Cancel();
        _filterDebounceCts = new CancellationTokenSource();
        var token = _filterDebounceCts.Token;
        var ui = TaskScheduler.FromCurrentSynchronizationContext();

        Task.Delay(400, token).ContinueWith(t =>
        {
            if (t.IsCanceled || token.IsCancellationRequested) return;
            RecomputeDisplayedBody();
        }, ui);
    }

    /// <summary>Cancels pending debounced JSONPath recompute. Each keystroke into the
    /// filter cancels the previous timer so we only run the filter ~400ms after the
    /// user stops typing.</summary>
    private CancellationTokenSource? _filterDebounceCts;
    partial void OnXmlPrettyPrintChanged(bool value) => RecomputeDisplayedBody();
    partial void OnResponseFormatChanged(string value)
    {
        RecomputeDisplayedBody();
        OnPropertyChanged(nameof(ResponseSyntaxHighlightingName));
        OnPropertyChanged(nameof(ResponseCanvasSyntaxKind));
    }

    private static string? TryApplyJsonPath(string json, string path)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is null) return null;
            var jp = global::Json.Path.JsonPath.Parse(path);
            var result = jp.Evaluate(node);
            if (result.Matches is null || result.Matches.Count == 0)
                return "// no matches";
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var match in result.Matches)
            {
                if (match.Value is null) arr.Add(null);
                else arr.Add(match.Value.DeepClone());
            }
            return arr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return $"// JSONPath error: {ex.Message}";
        }
    }

    private static string? TryPrettyPrintXml(string xml)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            return doc.ToString(System.Xml.Linq.SaveOptions.None);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves the raw response body bytes to a user-picked file. Suggests a filename
    /// based on the request URL host + path leaf + a content-type extension.</summary>
    public string SuggestedSaveFileName()
    {
        var ext = ContentTypeExtension(ResponseContentType);
        var url = Url ?? string.Empty;
        var stem = "response";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var leaf = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
            stem = string.IsNullOrEmpty(leaf) ? uri.Host : leaf;
        }
        return Sanitize(stem) + ext;
    }

    private static string ContentTypeExtension(string contentType) => contentType switch
    {
        var ct when ct.Contains("json") => ".json",
        var ct when ct.Contains("xml") => ".xml",
        var ct when ct.Contains("html") => ".html",
        var ct when ct.Contains("javascript") || ct.Contains("/js") => ".js",
        var ct when ct.Contains("css") => ".css",
        var ct when ct.StartsWith("image/png") => ".png",
        var ct when ct.StartsWith("image/jpeg") => ".jpg",
        var ct when ct.StartsWith("image/gif") => ".gif",
        var ct when ct.StartsWith("image/svg") => ".svg",
        var ct when ct.StartsWith("image/webp") => ".webp",
        var ct when ct.StartsWith("application/pdf") => ".pdf",
        var ct when ct.StartsWith("text/") => ".txt",
        _ => ".bin",
    };

    private static string Sanitize(string s)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>Scans the resolved URL, headers, and body for surviving <c>{{name}}</c>
    /// patterns. Returns the unique placeholder names (deduped, preserving discovery order)
    /// so the user can see exactly which variables are missing from their env / vars set.</summary>
    private static IReadOnlyList<string> CollectUnresolvedPlaceholders(
        string url,
        IEnumerable<KeyValuePair<string, string>> headers,
        string? body)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Scan(url);
        foreach (var (n, v) in headers) { Scan(n); Scan(v); }
        if (body is not null) Scan(body);
        return found;

        void Scan(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("{{")) return;
            var i = 0;
            while (i < text.Length - 1)
            {
                if (text[i] == '{' && text[i + 1] == '{')
                {
                    var close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                    if (close < 0) break;
                    var name = text.Substring(i + 2, close - (i + 2)).Trim();
                    if (name.Length > 0 && seen.Add(name)) found.Add(name);
                    i = close + 2;
                }
                else i++;
            }
        }
    }

    /// <summary>JSON snapshot of the full editor state, persisted on every Send for the
    /// History → Replay full-snapshot path. Serializes the same <see cref="RequestItem"/> the
    /// session-tab store uses, so EVERY request facet round-trips — method, URL, params,
    /// headers, body (all modes incl. multipart + file), auth credentials, request vars,
    /// scripts, settings and SOAP config. <see cref="ApplyRequestBlob"/> is the inverse.</summary>
    public string SerializeRequestBlob()
    {
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(BuildRequestItemFromVm());
        }
        catch
        {
            // Should not happen, but never let a serialization hiccup break the Send path.
            return "{}";
        }
    }

    /// <summary>Restores a full snapshot persisted by <see cref="SerializeRequestBlob"/>.
    /// Accepts both the current <see cref="RequestItem"/> shape (PascalCase keys) and the
    /// legacy hand-rolled shape (lowercase keys) emitted by older builds, so history recorded
    /// before this change still replays. Best-effort: bad JSON returns false.</summary>
    public bool ApplyRequestBlob(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        // Current format: a serialized RequestItem. Detect it by the PascalCase keys System.Text.Json
        // emits (Method/Url/Body) which the legacy lowercase format never had.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                && (root.TryGetProperty("Method", out _)
                    || root.TryGetProperty("Url", out _)
                    || root.TryGetProperty("Body", out _)))
            {
                var item = System.Text.Json.JsonSerializer.Deserialize<RequestItem>(json);
                if (item is not null)
                {
                    LoadFromRequestItem(item, sourcePath: null);
                    return true;
                }
            }
        }
        catch { /* fall through to the legacy parser */ }

        return ApplyLegacyRequestBlob(json);
    }

    /// <summary>Restores the pre-RequestItem blob shape (lowercase keys, auth captured only as a
    /// type string). Kept so history rows written by older builds still open and replay; new
    /// rows always use the full <see cref="RequestItem"/> snapshot.</summary>
    private bool ApplyLegacyRequestBlob(string json)
    {
        _loading = true;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("method", out var m)) Method = m.GetString() ?? Method;
            if (root.TryGetProperty("url", out var u)) Url = u.GetString() ?? string.Empty;
            if (root.TryGetProperty("headers", out var hs) && hs.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                Headers.Clear();
                foreach (var h in hs.EnumerateArray())
                {
                    var name = h.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    var val = h.TryGetProperty("Value", out var v) ? v.GetString() ?? "" : "";
                    var enabled = !h.TryGetProperty("IsActive", out var a) || a.GetBoolean();
                    Headers.Add(new KvEntry(name, val, enabled));
                }
            }
            if (root.TryGetProperty("body", out var b))
            {
                if (b.TryGetProperty("type", out var bt)) BodyType = bt.GetString() ?? "none";
                if (b.TryGetProperty("content", out var bc)) BodyContent = bc.GetString() ?? string.Empty;
                if (b.TryGetProperty("graphql", out var gql))
                {
                    if (gql.TryGetProperty("query", out var q)) GraphQLQuery = q.GetString() ?? string.Empty;
                    if (gql.TryGetProperty("vars", out var gv)) GraphQLVariables = gv.GetString() ?? string.Empty;
                }
            }
            // Legacy request vars were serialized but never restored before — restore them now.
            if (root.TryGetProperty("vars", out var vs) && vs.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                Variables.Clear();
                foreach (var vv in vs.EnumerateArray())
                {
                    var name = vv.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    var val = vv.TryGetProperty("Value", out var v) ? v.GetString() ?? "" : "";
                    var enabled = !vv.TryGetProperty("IsActive", out var a) || a.GetBoolean();
                    if (!string.IsNullOrEmpty(name)) Variables.Add(new KvEntry(name, val, enabled));
                }
            }
            if (root.TryGetProperty("auth", out var at)) AuthType = at.GetString() ?? "none";
            if (root.TryGetProperty("preRequestScript", out var prs)) PreRequestScript = prs.GetString() ?? string.Empty;
            if (root.TryGetProperty("postResponseScript", out var pors)) PostResponseScript = pors.GetString() ?? string.Empty;
            if (root.TryGetProperty("tests", out var ts)) TestsScript = ts.GetString() ?? string.Empty;
            if (root.TryGetProperty("docs", out var d)) Docs = d.GetString() ?? string.Empty;
            EnsureGhostRows();
            IsDirty = false;
            return true;
        }
        catch { return false; }
        finally { _loading = false; }
    }

    /// <summary>Parses Set-Cookie headers from the response into individual cookie rows so the
    /// Cookies subtab can list them. Domain falls back to the request host when the cookie
    /// itself doesn't specify one.</summary>
    private void RebuildResponseCookies(IReadOnlyList<KeyValuePair<string, string>> headers, Uri requestUri)
    {
        ResponseCookies.Clear();
        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            var parsed = ResponseCookieRow.Parse(value, requestUri.Host);
            if (parsed is not null) ResponseCookies.Add(parsed);
        }
    }

    /// <summary>Raised whenever the resolvable variable set changes (env switch or request-var edit).
    /// Lets variable-aware editors refresh their colorizer / autocomplete snapshot.</summary>
    public event EventHandler? VariablesSnapshotChanged;

    /// <summary>Raised after a Send when a pre-request or post-response script changed environment
    /// variables via <c>bru.setEnvVar</c> / <c>bru.deleteEnvVar</c>. Carries only the deltas so the
    /// host can fold them into the active environment and re-broadcast to open tabs — this is what
    /// makes a token extracted in a post-response script visible to the next request.</summary>
    public event EventHandler<EnvVarMutationEventArgs>? EnvironmentVariablesMutated;

    private IReadOnlyDictionary<string, string> _environmentVariables = new Dictionary<string, string>();

    /// <summary>External variables (e.g. active environment) merged in at Send time. Request vars win.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables
    {
        get => _environmentVariables;
        set
        {
            _environmentVariables = value;
            VariablesSnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Names of the environment variables flagged as secret in the active env(s).
    /// Set alongside <see cref="EnvironmentVariables"/>. Used to redact resolved secret
    /// values from copyable surfaces (codegen snippets, request preview).</summary>
    public IReadOnlyCollection<string> SecretVariableNames { get; set; } = Array.Empty<string>();

    /// <summary>Resolved values of the secret variables, ready to feed
    /// <see cref="Vegha.Core.Interpolation.SecretRedactor"/>. A secret bound to a
    /// <c>secret://</c> provider yields the URI (also worth masking — never the live secret).</summary>
    public IEnumerable<string> SecretValuesForRedaction =>
        SecretVariableNames
            .Select(n => EnvironmentVariables.TryGetValue(n, out var v) ? v : null)
            .Where(v => !string.IsNullOrEmpty(v))!
            .Cast<string>();

    public IReadOnlyList<string> Methods => SupportedMethods;
    public IReadOnlyList<string> AvailableBodyTypes => BodyTypes;

    public RequestEditorViewModel(
        HttpExecutor executor,
        OAuth2TokenAcquirer oauth2,
        JintHost scriptHost,
        ILogger<RequestEditorViewModel> logger,
        Vegha.Core.History.HistoryStore? historyStore = null,
        Vegha.Integrations.Secrets.SecretRegistry? secretRegistry = null)
    {
        _executor = executor;
        _oauth2 = oauth2;
        _scriptHost = scriptHost;
        _logger = logger;
        _historyStore = historyStore;
        _secretRegistry = secretRegistry;

        Variables.CollectionChanged += (_, _) =>
        {
            VariablesSnapshotChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(VarsTotalCount));
        };
        PostResponseVariables.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(VarsTotalCount));
        Params.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ParamsCount));
        Headers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HeadersCount));

        // Keep the test-results summary (Tests (N), Passed: X, Failed: Y) in sync.
        TestResults.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTestResults));
            OnPropertyChanged(nameof(TestsPassedCount));
            OnPropertyChanged(nameof(TestsFailedCount));
        };

        // Every request-side collection feeds the dirty flag: any add / remove of a row,
        // and any edit of a row's properties (Name / Value / Enabled / etc.), must enable
        // the Save button. Without this, users could change Params / Headers / Vars and
        // their edits never got persisted because Save stayed disabled. Mirrors what the
        // OnXxxChanged partial methods already do for scalar properties (Url / Method /
        // BodyContent / Auth fields). All hooks gate on !_loading so the initial population
        // from <see cref="LoadFromRequestItem"/> doesn't false-trigger dirty.
        WireDirtyTracking(Params);
        WireDirtyTracking(Headers);
        WireDirtyTracking(Variables);
        WireDirtyTracking(PostResponseVariables);
        WireDirtyTracking(FormUrlEncodedItems);
        WireDirtyTracking(MultipartItems);
        WireDirtyTracking(OAuth2TokenParameters);
        WireDirtyTracking(OAuth2RefreshParameters);

        // Ghost-row UX (Bruno parity): every KV table keeps a blank placeholder row at its
        // tail — typing into it spawns the next one, so there's no "+ Add row" step. Seeded
        // here (before any load) so scratch tabs that never call LoadFromRequestItem still
        // show the ghost; load paths re-ensure it inside their _loading guard. Multipart is
        // excluded — its rows need an explicit text-vs-file choice up front. The _loading
        // guard keeps the seed adds from tripping the dirty tracking wired just above.
        _loading = true;
        try { EnsureGhostRows(); }
        finally { _loading = false; }
        KvAutoAppend.Wire(Params, () => new KvEntry(), r => r.IsBlank, () => _loading);
        KvAutoAppend.Wire(Headers, () => new KvEntry(), r => r.IsBlank, () => _loading);
        KvAutoAppend.Wire(Variables, () => new KvEntry(), r => r.IsBlank, () => _loading);
        KvAutoAppend.Wire(PostResponseVariables, () => new KvEntry(), r => r.IsBlank, () => _loading);
        KvAutoAppend.Wire(FormUrlEncodedItems, () => new KvEntry(), r => r.IsBlank, () => _loading);
        KvAutoAppend.Wire(OAuth2TokenParameters, () => new OAuth2AdditionalParameter(), r => r.IsBlank, () => _loading);
        KvAutoAppend.Wire(OAuth2RefreshParameters, () => new OAuth2AdditionalParameter(), r => r.IsBlank, () => _loading);
    }

    /// <summary>Appends the trailing blank row to every auto-append KV table that lost it —
    /// called at construction and from the load paths (inside <c>_loading</c>) so the
    /// structural adds never count as user edits.</summary>
    private void EnsureGhostRows()
    {
        KvAutoAppend.EnsureTrailingBlank(Params, () => new KvEntry(), r => r.IsBlank);
        KvAutoAppend.EnsureTrailingBlank(Headers, () => new KvEntry(), r => r.IsBlank);
        KvAutoAppend.EnsureTrailingBlank(Variables, () => new KvEntry(), r => r.IsBlank);
        KvAutoAppend.EnsureTrailingBlank(PostResponseVariables, () => new KvEntry(), r => r.IsBlank);
        KvAutoAppend.EnsureTrailingBlank(FormUrlEncodedItems, () => new KvEntry(), r => r.IsBlank);
        KvAutoAppend.EnsureTrailingBlank(OAuth2TokenParameters, () => new OAuth2AdditionalParameter(), r => r.IsBlank);
        KvAutoAppend.EnsureTrailingBlank(OAuth2RefreshParameters, () => new OAuth2AdditionalParameter(), r => r.IsBlank);
    }

    /// <summary>Routes both collection-level (add/remove/clear) and item-level
    /// (PropertyChanged on each row) mutations to the dirty flag. The item-level hookup
    /// is what catches the user editing an existing row's text — without it, the Save
    /// button would stay disabled because the collection identity didn't change.</summary>
    private void WireDirtyTracking<T>(ObservableCollection<T> collection)
        where T : INotifyPropertyChanged
    {
        // Existing items (typically none at ctor time, but be defensive for tests).
        foreach (var item in collection) item.PropertyChanged += OnRowPropertyChanged;

        collection.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (T item in e.NewItems) item.PropertyChanged += OnRowPropertyChanged;
            if (e.OldItems is not null)
                foreach (T item in e.OldItems) item.PropertyChanged -= OnRowPropertyChanged;
            if (!_loading) IsDirty = true;
        };
    }

    /// <summary>Single shared handler for PropertyChanged on every tracked row. Marks the
    /// request dirty so Save lights up the moment the user types into a Headers / Params /
    /// Vars cell.</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_loading) IsDirty = true;
        // A row flipping between blank and non-blank changes the ghost-row-aware tab badges
        // even when the collection itself didn't change (e.g. the user cleared a cell).
        if (e.PropertyName == nameof(KvEntry.IsBlank))
        {
            OnPropertyChanged(nameof(ParamsCount));
            OnPropertyChanged(nameof(HeadersCount));
            OnPropertyChanged(nameof(VarsTotalCount));
        }
    }

    /// <summary>Posts the GraphQL introspection query to the current URL and renders a
    /// browseable summary of the schema (types + fields) into <see cref="GraphQLSchemaSummary"/>.
    /// Picks up the same headers + auth as a normal Send via the inheritance composer so
    /// the introspection request sails through the same auth as queries do.</summary>
    [RelayCommand]
    private async Task IntrospectGraphQLAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            ResponseStatusText = "Set the GraphQL endpoint URL first.";
            return;
        }
        ResponseStatusText = "Introspecting…";
        try
        {
            var composed = ComposeWithInheritance();
            var vars = BuildVariableSnapshot(composed);
            var resolvedUrl = Vegha.Core.Interpolation.Interpolator.Resolve(Url, vars);

            var headers = new List<KeyValuePair<string, string>>();
            foreach (var h in composed.Headers)
                if (h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                    headers.Add(new(h.Name,
                        Vegha.Core.Interpolation.Interpolator.Resolve(h.Value ?? string.Empty, vars)));

            var schema = await Vegha.Core.Requests.GraphQLIntrospector.IntrospectAsync(
                _executor, new Uri(resolvedUrl), headers, cancellationToken);

            GraphQLSchemaSummary = RenderSchemaSummary(schema);
            GraphQLSchemaLoaded = true;
            ResponseStatusText = $"Schema loaded — {schema.Types.Count} type(s).";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GraphQL introspection failed");
            ResponseStatusText = $"Introspection failed: {ex.Message}";
        }
    }

    private static string RenderSchemaSummary(Vegha.Core.Requests.GraphQLIntrospector.GraphQLSchema schema)
    {
        var sb = new StringBuilder();
        if (schema.QueryType is not null) sb.AppendLine($"# Query: {schema.QueryType}");
        if (schema.MutationType is not null) sb.AppendLine($"# Mutation: {schema.MutationType}");
        if (schema.SubscriptionType is not null) sb.AppendLine($"# Subscription: {schema.SubscriptionType}");
        sb.AppendLine();
        foreach (var t in schema.Types.OrderBy(x => x.Kind).ThenBy(x => x.Name))
        {
            sb.Append(t.Kind).Append(' ').AppendLine(t.Name);
            if (t.EnumValues.Count > 0)
            {
                sb.Append("    values: ").AppendLine(string.Join(", ", t.EnumValues));
            }
            foreach (var f in t.Fields)
            {
                sb.Append("    ").Append(f.Name);
                if (f.Args.Count > 0)
                    sb.Append('(')
                      .Append(string.Join(", ", f.Args.Select(a => $"{a.Name}: {a.TypeRef}")))
                      .Append(')');
                sb.Append(": ").AppendLine(f.TypeRef);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, string> BuildVariableSnapshot(
        Vegha.Core.Requests.RequestComposition.Composed composed) =>
        composed.Vars;

    /// <summary>Computes the env-var deltas a script produced — comparing the script's resulting
    /// env bag (<paramref name="after"/>) against the snapshot it ran against (<paramref name="before"/>)
    /// — and raises <see cref="EnvironmentVariablesMutated"/> when there's anything to apply. The
    /// script bag is a copy of <paramref name="before"/> with <c>setEnvVar</c> changes layered on and
    /// <c>deleteEnvVar</c> entries removed, so a key-by-key diff recovers exactly what the user changed.</summary>
    private void RaiseEnvVarMutations(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        if (EnvironmentVariablesMutated is null) return;

        Dictionary<string, string>? updated = null;
        foreach (var (key, value) in after)
        {
            if (!before.TryGetValue(key, out var old) || !string.Equals(old, value, StringComparison.Ordinal))
                (updated ??= new Dictionary<string, string>(StringComparer.Ordinal))[key] = value;
        }

        List<string>? removed = null;
        foreach (var key in before.Keys)
        {
            if (!after.ContainsKey(key))
                (removed ??= new List<string>()).Add(key);
        }

        if (updated is null && removed is null) return;

        EnvironmentVariablesMutated.Invoke(this, new EnvVarMutationEventArgs(
            (IReadOnlyDictionary<string, string>?)updated ?? new Dictionary<string, string>(),
            (IReadOnlyCollection<string>?)removed ?? Array.Empty<string>()));
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        // Compose the inheritance view (collection ⊕ folders ⊕ request) when the editor
        // is bound to a tab. For ad-hoc / draft tabs there's no parent context — the
        // composed view is just this request's own values, which is also the default.
        var composedView = ComposeWithInheritance();

        // Run pre-request script first — it can set vars that subsequent interpolation reads.
        // The composed pre-request script chains collection → folders → request scripts in order.
        IReadOnlyDictionary<string, string> scriptVars = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(composedView.PreRequestScript))
        {
            var requestVarsForScript = BuildVariableLookup(Variables);
            var scriptResult = _scriptHost.RunPreRequest(
                composedView.PreRequestScript!,
                EnvironmentVariables,
                requestVars: requestVarsForScript,
                cancellationToken: cancellationToken);

            if (!scriptResult.IsSuccess)
            {
                ErrorMessage = scriptResult.ErrorMessage ?? "Pre-request script failed";
                HasResponse = false;
                return;
            }
            scriptVars = scriptResult.RuntimeVariables;
            // Persist any bru.setEnvVar / deleteEnvVar the pre-request script performed so the
            // change outlives this request (and reaches sibling tabs), matching Bruno.
            RaiseEnvVarMutations(EnvironmentVariables, scriptResult.EnvVarMutations);
        }

        // Resolve all template placeholders before executing.
        // Precedence (last wins): env < composed (collection+folder vars) < script-runtime < request-level.
        var vars = MergeVariables(EnvironmentVariables, composedView.Vars, scriptVars, BuildVariableLookup(Variables));

        // Pre-resolve any secret://provider/path#field values against the configured secret
        // managers, so the synchronous interpolation below sees real secret values.
        if (_secretRegistry is not null)
            vars = await _secretRegistry.ResolveSecretsAsync(vars, cancellationToken);

        var composedUrl = ComposeUrl(Url, Params, vars);
        if (composedUrl is null) { ErrorMessage = "URL is empty."; HasResponse = false; return; }

        // Auth: prefer the composed config (which honored Inherit fallthrough) over the bare VM state.
        // For OAuth2, acquire a token first then apply as Bearer. AWS SigV4 is signed below
        // (after the body is composed, since the signature includes the body hash).
        AuthConfig? authToApply = composedView.Auth ?? BuildAuthConfig();
        if (AuthType == "oauth2")
        {
            // Apply the Bruno-parity panel additions: additional token params, token-id
            // isolation, token source selection, refresh URL. The acquirer reads them
            // and threads them through cache lookup / token-endpoint POST / refresh flow.
            var additionalTokenParams = OAuth2TokenParameters
                .Where(p => p.IsActive && !string.IsNullOrEmpty(p.Key))
                .Select(p => new OAuth2AdditionalParam(p.Key, p.Value, p.SendIn))
                .ToList();
            var additionalRefreshParams = OAuth2RefreshParameters
                .Where(p => p.IsActive && !string.IsNullOrEmpty(p.Key))
                .Select(p => new OAuth2AdditionalParam(p.Key, p.Value, p.SendIn))
                .ToList();
            var refreshUrl = string.IsNullOrWhiteSpace(OAuth2RefreshTokenUrl) ? null : OAuth2RefreshTokenUrl;

            OAuth2TokenResult token = OAuth2GrantType switch
            {
                "password" => await _oauth2.AcquirePasswordAsync(
                    new OAuth2PasswordConfig(
                        TokenUrl: OAuth2TokenUrl,
                        ClientId: OAuth2ClientId,
                        ClientSecret: OAuth2ClientSecret,
                        Username: OAuth2Username,
                        Password: OAuth2Password,
                        Scope: string.IsNullOrWhiteSpace(OAuth2Scope) ? null : OAuth2Scope,
                        CredentialsPlacement: OAuth2CredentialsPlacement,
                        AdditionalParameters: additionalTokenParams,
                        TokenId: OAuth2TokenId,
                        TokenSource: OAuth2TokenSource,
                        RefreshTokenUrl: refreshUrl,
                        RefreshParameters: additionalRefreshParams),
                    vars, cancellationToken),
                "authorization_code" => await _oauth2.AcquireAuthorizationCodeAsync(
                    new OAuth2AuthorizationCodeConfig(
                        AuthorizationUrl: OAuth2AuthorizationUrl,
                        TokenUrl: OAuth2TokenUrl,
                        ClientId: OAuth2ClientId,
                        ClientSecret: OAuth2ClientSecret,
                        CallbackUrl: OAuth2CallbackUrl,
                        Scope: string.IsNullOrWhiteSpace(OAuth2Scope) ? null : OAuth2Scope,
                        State: string.IsNullOrWhiteSpace(OAuth2State) ? null : OAuth2State,
                        UsePkce: OAuth2UsePkce,
                        CredentialsPlacement: OAuth2CredentialsPlacement,
                        AdditionalParameters: additionalTokenParams,
                        TokenId: OAuth2TokenId,
                        TokenSource: OAuth2TokenSource,
                        RefreshTokenUrl: refreshUrl,
                        RefreshParameters: additionalRefreshParams),
                    vars, cancellationToken),
                _ => await _oauth2.AcquireClientCredentialsAsync(
                    new OAuth2ClientCredentialsConfig(
                        TokenUrl: OAuth2TokenUrl,
                        ClientId: OAuth2ClientId,
                        ClientSecret: OAuth2ClientSecret,
                        Scope: string.IsNullOrWhiteSpace(OAuth2Scope) ? null : OAuth2Scope,
                        CredentialsPlacement: OAuth2CredentialsPlacement,
                        AdditionalParameters: additionalTokenParams,
                        TokenId: OAuth2TokenId,
                        TokenSource: OAuth2TokenSource,
                        RefreshTokenUrl: refreshUrl,
                        RefreshParameters: additionalRefreshParams),
                    vars, cancellationToken)
            };

            if (!token.IsSuccess || string.IsNullOrEmpty(token.AccessToken))
            {
                ErrorMessage = token.ErrorMessage ?? "OAuth2 token acquisition failed.";
                HasResponse = false;
                return;
            }

            // Surface the acquired token in the panel preview so users see what's in flight.
            OAuth2LastAccessToken = token.AccessToken!;
            OAuth2TokenType = string.IsNullOrEmpty(token.TokenType) ? "Bearer" : token.TokenType!;
            OAuth2DecodedPayload = JwtDecoder.PrettyPrintPayload(token.AccessToken!);

            // Inject into the outgoing request according to the panel's "Add token to" /
            // "Header Prefix" — defaults match the existing Bearer-on-Authorization behavior.
            authToApply = BuildOAuth2BearerAuth(token.AccessToken!);
        }

        // Apply auth — may add headers and/or query params (for API Key in queryparams placement).
        var authResult = AuthApplier.Apply(authToApply, composedUrl, vars);

        if (!Uri.TryCreate(authResult.Url, UriKind.Absolute, out var uri))
        {
            ErrorMessage = $"URL is not a valid absolute URI: {authResult.Url}";
            HasResponse = false;
            return;
        }

        // Link the command-supplied token so external cancellation (e.g. command re-issue)
        // still flows through, while letting the Cancel button on the response overlay
        // cancel via _sendCts.Cancel().
        _sendCts?.Dispose();
        _sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendToken = _sendCts.Token;

        IsSending = true;
        SendingElapsedSeconds = 0;
        ErrorMessage = null;

        // Live elapsed-seconds ticker for the busy overlay. Fire-and-forget; the CTS token
        // shuts it down when the request finishes or the user cancels. Avalonia's UI sync
        // context flows through await Task.Delay so property writes land on the UI thread.
        var elapsedSw = System.Diagnostics.Stopwatch.StartNew();
        _ = TickSendingElapsedAsync(elapsedSw, sendToken);

        try
        {
            var (body, contentType) = BodyType == "graphql"
                ? ComposeGraphQLBody(GraphQLQuery, GraphQLVariables, vars)
                : ComposeBody(BodyType, BodyContent, vars);

            // SOAP WS-Security / WS-Addressing — inject the configured headers (a fresh
            // Timestamp / nonce each send) into the envelope before it goes on the wire.
            var soapConfig = BuildSoapConfig();
            if (body is not null && Vegha.Core.Requests.SoapSecurityProcessor.HasOutgoing(soapConfig))
            {
                body = Vegha.Core.Requests.SoapSecurityProcessor.Apply(
                    body, soapConfig, v => Interpolator.Resolve(v, vars));
            }

            // Headers: composed inheritance view (collection ⊕ folders ⊕ request, last-wins)
            // already merged by RequestComposition. Run them through the same {{var}}
            // interpolation step the legacy ComposeHeaders did.
            var headers = composedView.Headers
                .Select(h => new KeyValuePair<string, string>(
                    Interpolator.Resolve(h.Name, vars),
                    Interpolator.Resolve(h.Value, vars)))
                .ToList();
            // Append auth headers after request headers — auth wins on conflict.
            foreach (var h in authResult.Headers) headers.Add(h);

            // Detect any {{var}} placeholders that survived interpolation — those are sent
            // as literal text and almost always cause server-side errors. Surface up front so
            // the user knows which variable is missing instead of debugging a generic 500.
            var unresolved = CollectUnresolvedPlaceholders(uri.ToString(), headers, body);
            if (unresolved.Count > 0)
            {
                ResponseStatusText = $"Unresolved variable(s): {string.Join(", ", unresolved)} — request sent with literal placeholders";
            }

            // AWS SigV4 needs the final URL + headers + body — sign here, after composition.
            if (AuthType == "awsv4")
            {
                var sigCfg = BuildAuthConfig();
                if (sigCfg is not null)
                {
                    var sig = AwsV4Signer.SignFromAuthConfig(
                        sigCfg, Method, uri, headers, body ?? string.Empty, vars);
                    if (sig is not null)
                    {
                        headers.Add(new KeyValuePair<string, string>("X-Amz-Date", sig.XAmzDate));
                        headers.Add(new KeyValuePair<string, string>("X-Amz-Content-Sha256", sig.XAmzContentSha256));
                        if (sig.XAmzSecurityToken is not null)
                            headers.Add(new KeyValuePair<string, string>("X-Amz-Security-Token", sig.XAmzSecurityToken));
                        headers.Add(new KeyValuePair<string, string>("Authorization", sig.Authorization));
                    }
                }
            }

            // OAuth1 also needs the final URL + method to compute the signature base string.
            // Sign here, after auth-applier headers + AWS sigv4 (which is mutually exclusive with OAuth1).
            if (AuthType == "oauth1" && !string.IsNullOrEmpty(OAuth1ConsumerKey))
            {
                var oauthHeader = OAuth1Signer.BuildAuthorizationHeader(
                    new OAuth1Signer.Config(
                        ConsumerKey: Interpolator.Resolve(OAuth1ConsumerKey, vars),
                        ConsumerSecret: Interpolator.Resolve(OAuth1ConsumerSecret, vars),
                        SignatureMethod: OAuth1SignatureMethod,
                        Token: string.IsNullOrEmpty(OAuth1Token) ? null : Interpolator.Resolve(OAuth1Token, vars),
                        TokenSecret: string.IsNullOrEmpty(OAuth1TokenSecret) ? null : Interpolator.Resolve(OAuth1TokenSecret, vars),
                        Realm: string.IsNullOrEmpty(OAuth1Realm) ? null : OAuth1Realm),
                    Method, uri.ToString());
                headers.Add(new KeyValuePair<string, string>("Authorization", oauthHeader));
            }

            // WSSE UsernameToken — header form (in-band SOAP envelope inclusion is the
            // SOAP workspace's job; the header path is what most non-SOAP APIs accept).
            if (AuthType == "wsse" && !string.IsNullOrEmpty(WsseUsername))
            {
                var nonceBytes = new byte[16];
                System.Security.Cryptography.RandomNumberGenerator.Fill(nonceBytes);
                var nonceB64 = Convert.ToBase64String(nonceBytes);
                var created = DateTime.UtcNow.ToString("o");
                var passwordResolved = Interpolator.Resolve(WssePassword, vars);
                var digestSrc = Encoding.UTF8.GetBytes(Convert.ToBase64String(nonceBytes) + created + passwordResolved);
                // Per WSSE spec: PasswordDigest = base64(sha1(nonce + created + password))
                var sha = System.Security.Cryptography.SHA1.HashData(
                    nonceBytes.Concat(Encoding.UTF8.GetBytes(created + passwordResolved)).ToArray());
                var passwordDigest = Convert.ToBase64String(sha);
                var wsseHeader = $"UsernameToken Username=\"{Interpolator.Resolve(WsseUsername, vars)}\", " +
                                 $"PasswordDigest=\"{passwordDigest}\", Nonce=\"{nonceB64}\", Created=\"{created}\"";
                headers.Add(new KeyValuePair<string, string>("X-WSSE", wsseHeader));
            }

            // Always pass options so the cookie jar engages for cookie-bearing requests.
            // NTLM creds (if the user picked NTLM) ride on the same options bag so the
            // executor can swap to HttpClientHandler-with-Credentials for that request.
            System.Net.NetworkCredential? ntlmCred = null;
            if (AuthType == "ntlm" && !string.IsNullOrEmpty(NtlmUsername))
            {
                ntlmCred = string.IsNullOrEmpty(NtlmDomain)
                    ? new System.Net.NetworkCredential(NtlmUsername, NtlmPassword)
                    : new System.Net.NetworkCredential(NtlmUsername, NtlmPassword, NtlmDomain);
            }

            // mTLS client certificate — load from PFX or PEM. Best-effort; failures
            // surface in the status message rather than aborting the request.
            System.Security.Cryptography.X509Certificates.X509Certificate2? clientCert = null;
            if (!string.IsNullOrWhiteSpace(MtlsCertPath) && File.Exists(MtlsCertPath))
            {
                try
                {
                    clientCert = System.IO.Path.GetExtension(MtlsCertPath).ToLowerInvariant() switch
                    {
                        // X509CertificateLoader replaced the X509Certificate2(path, password)
                        // constructor in .NET 9 — the old one is obsolete (SYSLIB0057).
                        ".pfx" or ".p12" => System.Security.Cryptography.X509Certificates.X509CertificateLoader
                            .LoadPkcs12FromFile(MtlsCertPath, MtlsCertPassword),
                        _ => System.Security.Cryptography.X509Certificates.X509Certificate2
                            .CreateFromPemFile(MtlsCertPath),
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load mTLS client cert from {Path}", MtlsCertPath);
                }
            }

            var options = new Vegha.Core.Requests.HttpRequestOptions(
                FollowRedirects: SettingFollowRedirects,
                VerifySsl: SettingVerifySsl,
                UseCookies: SettingSendCookies && SettingSaveCookies,
                NtlmCredential: ntlmCred,
                ClientCertificate: clientCert);

            // Pick the structured-body shape for non-text body modes. The executor reads
            // these in precedence order (FilePath > MultipartFields > FormFields > Body).
            IReadOnlyList<KeyValuePair<string, string>>? formFields = null;
            IReadOnlyList<Vegha.Core.Requests.MultipartField>? multipartFields = null;
            string? filePathToSend = null;
            string? fileContentTypeToSend = null;
            if (BodyType == "form-urlencoded")
            {
                formFields = FormUrlEncodedItems
                    .Where(f => f.IsActive && !string.IsNullOrEmpty(f.Name))
                    .Select(f => new KeyValuePair<string, string>(
                        f.Name, vars.Count > 0 ? Interpolator.Resolve(f.Value, vars) : f.Value))
                    .ToList();
                body = null;        // structured form replaces the raw body
                contentType = null; // FormUrlEncodedContent self-sets correct content-type
            }
            else if (BodyType == "multipart-form")
            {
                multipartFields = MultipartItems
                    .Where(m => m.IsActive && !string.IsNullOrEmpty(m.Name))
                    .Select(m => new Vegha.Core.Requests.MultipartField(
                        m.Name,
                        vars.Count > 0 ? Interpolator.Resolve(m.Value, vars) : m.Value,
                        m.Kind,
                        string.IsNullOrEmpty(m.ContentType) ? null : m.ContentType))
                    .ToList();
                body = null;
                contentType = null;
            }
            else if (BodyType == "file" && !string.IsNullOrEmpty(FilePath))
            {
                filePathToSend = FilePath;
                fileContentTypeToSend = string.IsNullOrEmpty(FileContentType) ? null : FileContentType;
                body = null;
                contentType = fileContentTypeToSend; // executor uses ContentType for file uploads too
            }

            var result = await _executor.ExecuteAsync(
                new HttpExecutionRequest(
                    new HttpMethod(Method),
                    uri,
                    Headers: headers,
                    Body: body,
                    ContentType: contentType,
                    FormFields: formFields,
                    MultipartFields: multipartFields,
                    FilePath: filePathToSend,
                    Options: options),
                sendToken).ConfigureAwait(true);

            // Digest auth: 401 → parse WWW-Authenticate challenge → resend with response header.
            // The first leg is the deliberate "ping" that surfaces realm + nonce; final timing
            // and body come from the second response.
            if (AuthType == "digest" && result.StatusCode == 401)
            {
                var digestRetry = TryBuildDigestRetry(result, uri);
                if (digestRetry is not null)
                {
                    headers.Add(new KeyValuePair<string, string>("Authorization", digestRetry));
                    result = await _executor.ExecuteAsync(
                        new HttpExecutionRequest(
                            new HttpMethod(Method),
                            uri,
                            Headers: headers,
                            Body: body,
                            ContentType: contentType,
                            Options: options),
                        sendToken).ConfigureAwait(true);
                }
            }

            ResponseStatusCode = result.StatusCode;
            ResponseStatusText = result.ReasonPhrase;
            ResponseBody = result.Body;
            ResponseBodyBytes = result.BodyBytes ?? Array.Empty<byte>();
            ResponseContentType = result.ContentType ?? string.Empty;
            ResponseElapsedMilliseconds = result.ElapsedMilliseconds;
            ErrorMessage = result.ErrorMessage;

            var timing = result.Timing ?? Vegha.Core.Requests.HttpExecutionTiming.Zero;
            TimingDnsMs = timing.DnsMs;
            TimingConnectMs = timing.ConnectMs;
            TimingTlsMs = timing.TlsMs;
            TimingTtfbMs = timing.TtfbMs;
            TimingContentMs = timing.ContentMs;
            TimingTotalMs = timing.TotalMs;
            RebuildTimelinePhases(timing);

            ResponseHeaders.Clear();
            foreach (var (name, value) in result.Headers)
            {
                ResponseHeaders.Add(new HeaderRow(name, value));
            }
            RebuildResponseCookies(result.Headers, uri);

            RawResponseText = BuildRawResponseText(result);
            SentRequestText = result.SentRequestText ?? string.Empty;
            HasResponse = true;

            // Persist to history (best-effort) including a JSON snapshot of the request so
            // Replay can rebuild the full state (method/url/headers/body/auth/scripts).
            if (_historyStore is not null)
            {
                try
                {
                    var requestBlob = SerializeRequestBlob();
                    await _historyStore.AppendAsync(
                        Method,
                        uri.ToString(),
                        result.StatusCode,
                        result.ElapsedMilliseconds,
                        result.Body,
                        result.ErrorMessage,
                        sendToken,
                        requestBlob,
                        HistoryWorkspaceIdProvider?.Invoke()).ConfigureAwait(true);
                }
                catch (Exception histEx)
                {
                    _logger.LogWarning(histEx, "Failed to append history");
                }
            }

            // Run post-response script + tests block (if either is present). Inside JintHost,
            // postScript runs FIRST, then tests — so `bru.setVar` / extracted state set by
            // post-response is visible inside `test(…)` assertion blocks.
            // The composed scripts chain workspace → collection → folders → request.
            TestResults.Clear();
            var hasPost = !string.IsNullOrWhiteSpace(composedView.PostResponseScript);
            var hasTests = !string.IsNullOrWhiteSpace(composedView.TestsScript);
            if (hasPost || hasTests)
            {
                var resApi = new ResponseApi(
                    result.StatusCode, result.ReasonPhrase, result.Body,
                    result.ElapsedMilliseconds, result.Headers, uri.ToString());
                var post = _scriptHost.RunPostResponse(
                    postScript: composedView.PostResponseScript,
                    testsScript: composedView.TestsScript,
                    response: resApi,
                    envVars: EnvironmentVariables,
                    requestVars: vars,
                    cancellationToken: sendToken);

                foreach (var t in post.TestOutcomes)
                    TestResults.Add(new TestResultRow(t.Name, t.Passed, t.FailureMessage, t.DurationMs));

                if (!post.IsSuccess && string.IsNullOrEmpty(ErrorMessage))
                    ErrorMessage = post.ErrorMessage;

                // Surface bru.setEnvVar / deleteEnvVar mutations so the host folds them into the
                // active environment and re-broadcasts. Without this, a token extracted here is
                // dropped and {{access_token}} resolves as unset on the next request.
                RaiseEnvVarMutations(EnvironmentVariables, post.EnvVarMutations);
            }
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Request canceled.";
            HasResponse = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing request to {Url}", uri);
            ErrorMessage = ex.Message;
            HasResponse = false;
        }
        finally
        {
            IsSending = false;
            elapsedSw.Stop();
            // Cancel + dispose the linked CTS so the ticker loop exits cleanly. Null-out
            // the field so CanCancelSend stops finding a live source after IsSending flips.
            try { _sendCts?.Cancel(); } catch { /* best-effort */ }
            _sendCts?.Dispose();
            _sendCts = null;
        }
    }

    /// <summary>Cancels the in-flight request. Bound to the "Cancel Request" button in the
    /// response panel's busy overlay. CanExecute mirrors <see cref="IsSending"/> so the
    /// button only lights up while a request is actually outstanding.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelSend))]
    private void CancelSend()
    {
        try { _sendCts?.Cancel(); } catch { /* best-effort */ }
    }

    private bool CanCancelSend() => IsSending;

    // ---- Run & inspect ----------------------------------------------------------------
    // Runs a single script slot (pre-request / post-response / tests) in isolation against the
    // current variables and the LAST captured response, surfacing console output, the resulting
    // variables, and test results. Mutations are NOT persisted to the environment — env-var
    // changes show as a preview only. Reuses the same JintHost the Send path uses.

    /// <summary>Runs the pre-request script and shows its console output + variable mutations.</summary>
    [RelayCommand]
    private void RunPreRequestInspect()
    {
        var composed = ComposeWithInheritance();
        if (string.IsNullOrWhiteSpace(composed.PreRequestScript))
        {
            ShowInspectHint("Pre-request run", "No pre-request script to run.");
            return;
        }

        var requestVars = BuildVariableLookup(Variables);
        var result = _scriptHost.RunPreRequest(
            composed.PreRequestScript!,
            EnvironmentVariables,
            requestVars: requestVars);

        PopulateInspectResults(
            "Pre-request run",
            result.IsSuccess, result.ErrorMessage,
            result.ConsoleMessages, result.RuntimeVariables, result.EnvVarMutations,
            testOutcomes: null);
    }

    /// <summary>Runs the post-response script against the last response.</summary>
    [RelayCommand]
    private void RunPostResponseInspect() => RunPostOrTestsInspect("Post-response run", includeTests: false);

    /// <summary>Runs the tests script against the last response.</summary>
    [RelayCommand]
    private void RunTestsInspect() => RunPostOrTestsInspect("Tests run", includeTests: true);

    private void RunPostOrTestsInspect(string title, bool includeTests)
    {
        var composed = ComposeWithInheritance();
        var script = includeTests ? composed.TestsScript : composed.PostResponseScript;
        if (string.IsNullOrWhiteSpace(script))
        {
            ShowInspectHint(title, includeTests ? "No tests to run." : "No post-response script to run.");
            return;
        }
        if (!HasResponse)
        {
            ShowInspectHint(title, "Send the request first — post-response and tests need the last response.");
            return;
        }

        var resApi = new ResponseApi(
            ResponseStatusCode,
            ResponseStatusText,
            ResponseBody,
            ResponseElapsedMilliseconds,
            ResponseHeaders.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)),
            Url);

        var requestVars = BuildVariableLookup(Variables);
        var result = _scriptHost.RunPostResponse(
            postScript: includeTests ? null : composed.PostResponseScript,
            testsScript: includeTests ? composed.TestsScript : null,
            response: resApi,
            envVars: EnvironmentVariables,
            requestVars: requestVars);

        PopulateInspectResults(
            title,
            result.IsSuccess, result.ErrorMessage,
            result.ConsoleMessages, result.RuntimeVariables, result.EnvVarMutations,
            result.TestOutcomes);
    }

    /// <summary>Closes the run-output panel.</summary>
    [RelayCommand]
    private void CloseInspectPanel() => IsInspectPanelOpen = false;

    private void ShowInspectHint(string title, string message)
    {
        InspectConsole.Clear();
        InspectVariables.Clear();
        InspectTests.Clear();
        InspectHasConsole = false;
        InspectHasVariables = false;
        InspectHasTests = false;
        InspectTitle = title;
        InspectMessage = message;
        IsInspectPanelOpen = true;
    }

    private void PopulateInspectResults(
        string title,
        bool success,
        string? errorMessage,
        IReadOnlyList<ConsoleMessage> console,
        IReadOnlyDictionary<string, string> runtimeVars,
        IReadOnlyDictionary<string, string> envMutations,
        IReadOnlyList<TestOutcome>? testOutcomes)
    {
        InspectTitle = title;
        InspectMessage = success ? null : errorMessage ?? "Script failed.";

        InspectConsole.Clear();
        foreach (var m in console) InspectConsole.Add(new ConsoleLineRow(m.Level, m.Text));

        InspectVariables.Clear();
        foreach (var (k, v) in runtimeVars.OrderBy(p => p.Key, StringComparer.Ordinal))
            InspectVariables.Add(new InspectVarRow("Runtime", k, v));
        // Env-var mutations as a preview diff (not persisted by inspect).
        foreach (var (k, v) in EnvVarDelta(EnvironmentVariables, envMutations)
            .OrderBy(p => p.Key, StringComparer.Ordinal))
            InspectVariables.Add(new InspectVarRow("Env (preview)", k, v));

        InspectTests.Clear();
        if (testOutcomes is not null)
            foreach (var t in testOutcomes)
                InspectTests.Add(new TestResultRow(t.Name, t.Passed, t.FailureMessage, t.DurationMs));

        InspectHasConsole = InspectConsole.Count > 0;
        InspectHasVariables = InspectVariables.Count > 0;
        InspectHasTests = InspectTests.Count > 0;
        IsInspectPanelOpen = true;
    }

    /// <summary>Added/changed env vars in <paramref name="after"/> vs <paramref name="before"/> —
    /// the script's setEnvVar deltas, for display only.</summary>
    private static IReadOnlyDictionary<string, string> EnvVarDelta(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var delta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in after)
            if (!before.TryGetValue(k, out var old) || !string.Equals(old, v, StringComparison.Ordinal))
                delta[k] = v;
        return delta;
    }

    /// <summary>Pumps <see cref="SendingElapsedSeconds"/> ~10×/s for the busy overlay.
    /// Exits when the linked token cancels (request finished or user clicked Cancel).
    /// Continuations land on the UI thread courtesy of the captured Avalonia sync context,
    /// so the OnPropertyChanged fire is safe without explicit dispatcher posts.</summary>
    private async Task TickSendingElapsedAsync(System.Diagnostics.Stopwatch sw, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                SendingElapsedSeconds = sw.Elapsed.TotalSeconds;
                await Task.Delay(100, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { /* expected on cancel/finish */ }
        catch { /* swallow — ticker is best-effort UI candy */ }
    }

    /// <summary>Raised when Save is invoked on a request that has no file yet (a "+" scratch
    /// draft). The host turns this into the "Save to collection…" flow — the editor itself can't
    /// pick a destination. File-backed requests save in place and never raise this.</summary>
    public event EventHandler? SaveAsRequested;

    /// <summary>Saves the current request as .bru text to <see cref="SourcePath"/>, or — when the
    /// request isn't backed by a file (a scratch draft) — asks the host to promote it into a
    /// collection via <see cref="SaveAsRequested"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(SourcePath))
        {
            // No on-disk home yet → let the host run the Save-to-collection flow.
            SaveAsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var item = BuildRequestItemFromVm();
        var bru = BruEmitter.Emit(item);
        try
        {
            await File.WriteAllTextAsync(SourcePath, bru).ConfigureAwait(true);
            IsDirty = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save request to {Path}", SourcePath);
            ErrorMessage = $"Save failed: {ex.Message}";
        }
    }

    // Enabled whenever there are unsaved edits. A file-backed request saves in place; a scratch
    // draft (no SourcePath) routes to the host's Save-to-collection flow.
    private bool CanSave() => IsDirty;

    [RelayCommand]
    private void AddHeader() => Headers.Add(new KvEntry());

    [RelayCommand]
    private void RemoveHeader(KvEntry? entry)
    {
        if (entry is not null) Headers.Remove(entry);
    }

    [RelayCommand]
    private void AddParam() => Params.Add(new KvEntry());

    [RelayCommand]
    private void RemoveParam(KvEntry? entry)
    {
        if (entry is not null) Params.Remove(entry);
    }

    // Request-scoped variables (pre-request vars in the Bru format) — exposed through the
    // existing Variables collection. AddVariable / RemoveVariable mirror the param + header
    // command pair so the Vars tab can reuse the KvEditor template directly.
    /// <summary>Resolves an XML declaration encoding name to a real <see cref="System.Text.Encoding"/>.
    /// Falls back to UTF-8 when the name is missing or unrecognized.</summary>
    private static System.Text.Encoding ResolveEncoding(string? name)
    {
        if (string.IsNullOrEmpty(name)) return new System.Text.UTF8Encoding(false);
        try { return System.Text.Encoding.GetEncoding(name); }
        catch { return new System.Text.UTF8Encoding(false); }
    }

    /// <summary>StringWriter that reports a caller-chosen <see cref="System.Text.Encoding"/>
    /// instead of the default UTF-16. <see cref="System.Xml.XmlWriter"/> reads this property
    /// to populate the <c>encoding="…"</c> attribute in the XML declaration — without the
    /// override, a UTF-8 document would silently become UTF-16 after a Prettify round-trip.</summary>
    private sealed class EncodingStringWriter : System.IO.StringWriter
    {
        private readonly System.Text.Encoding _encoding;
        public EncodingStringWriter(System.Text.Encoding encoding) { _encoding = encoding; }
        public override System.Text.Encoding Encoding => _encoding;
    }

    [RelayCommand]
    private void AddVariable() => Variables.Add(new KvEntry());

    [RelayCommand]
    private void RemoveVariable(KvEntry? entry)
    {
        if (entry is not null) Variables.Remove(entry);
    }

    [RelayCommand]
    private void AddPostResponseVariable() => PostResponseVariables.Add(new KvEntry());

    [RelayCommand]
    private void RemovePostResponseVariable(KvEntry? entry)
    {
        if (entry is not null) PostResponseVariables.Remove(entry);
    }

    /// <summary>The AvaloniaEdit syntax-highlighting definition name for the current body type.</summary>
    public string? BodySyntaxHighlightingName => SyntaxHighlightForBodyType(BodyType);

    /// <summary>True when the body editor should render an editable raw text surface
    /// (json/xml/text/sparql). False for form / file / no-body — those use a different UI.</summary>
    public bool IsBodyRaw => IsRawBodyType(BodyType);

    /// <summary>Drives visibility of the JSON→XML conversion button (XML body only).</summary>
    public bool IsBodyXml => BodyType == "xml";

    // Most-recent XML-shaped value seen on BodyContent. Captured so the user can paste JSON
    // over an XML envelope and run the explicit JSON→XML conversion: we use this snapshot
    // as the namespace template even though BodyContent itself is now the JSON. Cleared
    // when a new request loads (the load reassigns BodyContent and the snapshot updates
    // automatically through OnBodyContentChanged).
    private string? _xmlBodySnapshot;

    /// <summary>Display label for the body-type button (e.g. "JSON" not "json"). Form / file
    /// modes show their human label.</summary>
    public string BodyTypeDisplay => BodyType switch
    {
        "none"            => "No Body",
        "json"            => "JSON",
        "xml"             => "XML",
        "text"            => "TEXT",
        "sparql"          => "SPARQL",
        "multipart-form"  => "Multipart Form",
        "form-urlencoded" => "Form URL Encoded",
        "file"            => "File / Binary",
        "graphql"         => "GraphQL",
        _                 => BodyType,
    };

    /// <summary>Reformats <see cref="BodyContent"/> in place when the active body type has a
    /// known formatter. JSON → System.Text.Json with indentation; XML → XDocument round-trip;
    /// other types are no-ops. Errors keep the existing content and surface a status message
    /// (today via the editor's response status field).</summary>
    [RelayCommand]
    private void Prettify()
    {
        if (string.IsNullOrEmpty(BodyContent)) return;
        try
        {
            switch (BodyType)
            {
                case "json":
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(BodyContent);
                    BodyContent = System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    break;
                }
                case "xml":
                {
                    BodyContent = FormatXml(BodyContent);
                    break;
                }
                case "graphql":
                {
                    if (!string.IsNullOrEmpty(GraphQLVariables))
                    {
                        try
                        {
                            using var d = System.Text.Json.JsonDocument.Parse(GraphQLVariables);
                            GraphQLVariables = System.Text.Json.JsonSerializer.Serialize(d.RootElement,
                                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        }
                        catch { /* leave unchanged */ }
                    }
                    break;
                }
                // text, sparql, form-* and file types have no canonical formatter we can apply
                // safely — leave the content alone.
            }
        }
        catch (Exception ex)
        {
            ResponseStatusText = $"Prettify failed: {ex.Message}";
        }
    }

    /// <summary>Sets BodyType from a UI menu pick. Lets the AXAML hook a CommandParameter
    /// instead of needing per-option commands.</summary>
    [RelayCommand]
    private void SetBodyType(string? type)
    {
        if (!string.IsNullOrEmpty(type)) BodyType = type;
    }

    /// <summary>Converts JSON pasted into the XML body to SOAP XML using namespaces from the
    /// last-seen XML envelope (snapshotted on every BodyContent change). Triggered by the
    /// JSON→XML toolbar button — explicit so the user controls when conversion happens
    /// instead of relying on paste interception.</summary>
    [RelayCommand]
    private void ConvertJsonBodyToXml()
    {
        var template = _xmlBodySnapshot;
        if (string.IsNullOrWhiteSpace(template))
        {
            ResponseStatusText = "JSON→XML: no XML envelope to use as a namespace template.";
            return;
        }
        if (string.IsNullOrWhiteSpace(BodyContent))
        {
            ResponseStatusText = "JSON→XML: body is empty — paste JSON first.";
            return;
        }
        try
        {
            var converted = JsonToSoapBodyConverter.Convert(template, BodyContent);
            if (string.IsNullOrEmpty(converted))
            {
                ResponseStatusText = "JSON→XML: conversion failed (body is not valid JSON, or no operation root in the template).";
                return;
            }
            // Indent the result so the user doesn't need a separate Prettify click. If
            // formatting fails (shouldn't, since the converter just produced this XML), fall
            // back to the unformatted output.
            try { converted = FormatXml(converted); } catch { /* keep raw */ }
            BodyContent = converted;
            ResponseStatusText = "JSON→XML: converted.";
        }
        catch (Exception ex)
        {
            ResponseStatusText = $"JSON→XML failed: {ex.Message}";
        }
    }

    /// <summary>Reformats an XML string with two-space indentation, preserving any XML
    /// declaration (and its encoding) the input had. Shared between Prettify and the
    /// JSON→XML conversion command — the wire-encoding handling matters because StringWriter
    /// defaults to UTF-16, which XmlWriter would echo into the declaration's encoding
    /// attribute (silently turning utf-8 into utf-16).</summary>
    private static string FormatXml(string xml)
    {
        // LoadOptions.None drops insignificant whitespace so XmlWriter's Indent=true can lay
        // out fresh indentation. PreserveWhitespace would freeze the original layout in
        // place as text nodes and reformatting would become a no-op visually.
        var xdoc = System.Xml.Linq.XDocument.Parse(xml, System.Xml.Linq.LoadOptions.None);
        var encoding = ResolveEncoding(xdoc.Declaration?.Encoding);
        var settings = new System.Xml.XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = xdoc.Declaration is null,
            Encoding = encoding,
            NewLineHandling = System.Xml.NewLineHandling.Replace,
        };
        using var sw = new EncodingStringWriter(encoding);
        using (var xw = System.Xml.XmlWriter.Create(sw, settings))
            xdoc.Save(xw);
        return sw.ToString();
    }

    private bool CanSend() => !IsSending && !string.IsNullOrWhiteSpace(Url);

    partial void OnUrlChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        if (!_loading) IsDirty = true;

        // Paste-as-curl: when the URL field receives a string that starts with "curl ",
        // parse it and apply (method, headers, cookies, body, auth) instead of treating
        // the whole curl invocation as a URL. Reentrancy guard on _loading prevents
        // the apply-time Url=cleanUrl assignment from re-triggering.
        if (!_loading && !_applyingCurl && CurlImporter.LooksLikeCurl(value))
        {
            TryApplyPastedCurl(value);
        }
    }

    private bool _applyingCurl;

    private void TryApplyPastedCurl(string text)
    {
        if (!CurlImporter.TryParse(text, out var item) || item is null) return;
        _applyingCurl = true;
        try
        {
            // Preserve any existing SourcePath so the imported request can be saved over
            // the current file. LoadFromRequestItem clears IsDirty; we re-arm it after
            // since the pasted curl is unsaved by definition.
            var path = SourcePath;
            LoadFromRequestItem(item, path);
            IsDirty = true;
        }
        finally
        {
            _applyingCurl = false;
        }
    }

    partial void OnMethodChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnBodyTypeChanged(string value)
    {
        if (!_loading) IsDirty = true;
        OnPropertyChanged(nameof(BodySyntaxHighlightingName));
        OnPropertyChanged(nameof(BodyTypeDisplay));
        OnPropertyChanged(nameof(IsBodyRaw));
        OnPropertyChanged(nameof(IsBodyXml));
        OnPropertyChanged(nameof(BodyHasData));
    }
    partial void OnBodyContentChanged(string value)
    {
        if (!_loading) IsDirty = true;
        if (LooksLikeXml(value)) _xmlBodySnapshot = value;
    }

    private static bool LooksLikeXml(string content)
    {
        // Cheap shape check used to capture the XML body snapshot — we don't want to pay
        // for XDocument.Parse on every keystroke. The first non-whitespace char being '<'
        // is enough; the converter does proper parsing later.
        for (var i = 0; i < content.Length; i++)
        {
            if (char.IsWhiteSpace(content[i])) continue;
            return content[i] == '<';
        }
        return false;
    }
    partial void OnGraphQLQueryChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnGraphQLVariablesChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnAuthTypeChanged(string value)
    {
        if (!_loading) IsDirty = true;
        OnPropertyChanged(nameof(AuthHasData));
    }
    partial void OnBearerTokenChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnBasicUsernameChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnBasicPasswordChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnDigestUsernameChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnDigestPasswordChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnNtlmUsernameChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnNtlmPasswordChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnNtlmDomainChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth1ConsumerKeyChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth1ConsumerSecretChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth1TokenChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth1TokenSecretChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth1SignatureMethodChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth1RealmChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnWsseUsernameChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnWssePasswordChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnMtlsCertPathChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnMtlsCertPasswordChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnApiKeyNameChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnApiKeyValueChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnApiKeyPlacementChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2GrantTypeChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2TokenUrlChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2ClientIdChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2ClientSecretChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2ScopeChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2CredentialsPlacementChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2UsernameChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2PasswordChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2AuthorizationUrlChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2CallbackUrlChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2StateChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnOAuth2UsePkceChanged(bool value) { if (!_loading) IsDirty = true; }
    partial void OnSettingFollowRedirectsChanged(bool value) { if (!_loading) IsDirty = true; }
    partial void OnSettingVerifySslChanged(bool value) { if (!_loading) IsDirty = true; }
    partial void OnSettingEncodeUrlChanged(bool value) { if (!_loading) IsDirty = true; }
    partial void OnSettingSendCookiesChanged(bool value) { if (!_loading) IsDirty = true; }
    partial void OnSettingSaveCookiesChanged(bool value) { if (!_loading) IsDirty = true; }
    partial void OnSettingHttp2Changed(bool value) { if (!_loading) IsDirty = true; }
    partial void OnPreRequestScriptChanged(string value)
    {
        if (!_loading) IsDirty = true;
        OnPropertyChanged(nameof(PreRequestScriptHasData));
    }
    partial void OnPostResponseScriptChanged(string value)
    {
        if (!_loading) IsDirty = true;
        OnPropertyChanged(nameof(PostResponseScriptHasData));
    }
    partial void OnTestsScriptChanged(string value)
    {
        if (!_loading) IsDirty = true;
        OnPropertyChanged(nameof(TestsScriptHasData));
    }
    partial void OnDocsChanged(string value)
    {
        if (!_loading) IsDirty = true;
        OnPropertyChanged(nameof(DocsHasData));
    }
    partial void OnAwsAccessKeyIdChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnAwsSecretAccessKeyChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnAwsRegionChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnAwsServiceChanged(string value) { if (!_loading) IsDirty = true; }
    partial void OnAwsSessionTokenChanged(string value) { if (!_loading) IsDirty = true; }

    /// <summary>True while a request is being loaded from disk — suppresses dirty flag.</summary>
    private bool _loading;

    /// <summary>Sets all VM fields from a loaded request, suppressing dirty tracking during the apply.</summary>
    public void LoadFromRequestItem(RequestItem item, string? sourcePath = null)
    {
        _loading = true;
        try
        {
            Method = string.IsNullOrEmpty(item.Method) ? "GET" : item.Method;
            Url = item.Url;
            BodyType = item.Body.Mode switch
            {
                BodyMode.Json           => "json",
                BodyMode.Text           => "text",
                BodyMode.Xml            => "xml",
                BodyMode.Sparql         => "sparql",
                BodyMode.GraphQL        => "graphql",
                BodyMode.FormUrlEncoded => "form-urlencoded",
                BodyMode.MultipartForm  => "multipart-form",
                BodyMode.Binary         => "file",
                _                       => "none",
            };
            BodyContent      = item.Body.Content ?? string.Empty;
            GraphQLQuery     = item.Body.GraphQLQuery ?? string.Empty;
            GraphQLVariables = item.Body.GraphQLVariables ?? string.Empty;

            FormUrlEncodedItems.Clear();
            foreach (var f in item.Body.FormData)
                FormUrlEncodedItems.Add(new KvEntry(f.Name, f.Value, f.Enabled));

            MultipartItems.Clear();
            foreach (var m in item.Body.MultipartItems)
                MultipartItems.Add(new MultipartFormRow
                {
                    Name = m.Name,
                    Value = m.Value,
                    Kind = string.IsNullOrEmpty(m.Kind) ? "text" : m.Kind,
                    ContentType = m.ContentType ?? string.Empty,
                    IsActive = m.Enabled,
                });

            FilePath        = item.Body.FilePath ?? string.Empty;
            FileContentType = item.Body.FileContentType ?? string.Empty;

            Headers.Clear();
            foreach (var h in item.Headers) Headers.Add(new KvEntry(h.Name, h.Value, h.Enabled));
            Params.Clear();
            foreach (var p in item.Params) Params.Add(new KvEntry(p.Name, p.Value, p.Enabled));
            Variables.Clear();
            foreach (var v in item.PreRequestVars) Variables.Add(new KvEntry(v.Name, v.Value, v.Enabled));
            PostResponseVariables.Clear();
            foreach (var v in item.PostResponseVars) PostResponseVariables.Add(new KvEntry(v.Name, v.Value, v.Enabled));

            ApplyAuthConfig(item.Auth);
            PreRequestScript = item.PreRequestScript ?? string.Empty;
            PostResponseScript = item.PostResponseScript ?? string.Empty;
            TestsScript = item.Tests ?? string.Empty;
            Docs = item.Docs ?? string.Empty;

            SettingFollowRedirects = item.Settings.FollowRedirects;
            SettingVerifySsl       = item.Settings.VerifySsl;
            SettingEncodeUrl       = item.Settings.EncodeUrl;
            SettingSendCookies     = item.Settings.SendCookies;
            SettingSaveCookies     = item.Settings.SaveCookies;
            SettingHttp2           = item.Settings.EnableHttp2;

            var soap = item.Soap;
            SoapTimestampEnabled     = soap?.Timestamp is not null;
            SoapTimestampTtl         = soap?.Timestamp?.TimeToLiveSeconds ?? 60;
            SoapUsernameTokenEnabled = soap?.UsernameToken is not null;
            SoapWssUsername          = soap?.UsernameToken?.Username ?? string.Empty;
            SoapWssPassword          = soap?.UsernameToken?.Password ?? string.Empty;
            SoapWssPasswordType      = soap?.UsernameToken?.PasswordType == WssPasswordType.Digest ? "digest" : "text";
            SoapWssAddNonce          = soap?.UsernameToken?.AddNonce ?? true;
            SoapWssAddCreated        = soap?.UsernameToken?.AddCreated ?? true;
            SoapAddressingEnabled    = soap?.Addressing is not null;
            SoapWsaAction            = soap?.Addressing?.Action ?? string.Empty;
            SoapWsaTo                = soap?.Addressing?.To ?? string.Empty;
            SoapWsaReplyTo           = soap?.Addressing?.ReplyTo ?? string.Empty;
            SoapWsaMessageId         = soap?.Addressing?.MessageId ?? string.Empty;
            SoapWsaAutoMessageId     = soap?.Addressing?.AutoMessageId ?? true;

            // Trailing ghost rows come back after the Clear+repopulate above. Still inside
            // _loading so the adds don't mark the freshly-loaded request dirty.
            EnsureGhostRows();

            _loadedItem = item;
            SourcePath = sourcePath;
            IsDirty = false;
            OnPropertyChanged(nameof(IsSoapRequest));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The original RequestItem this VM was loaded from; preserves fields the VM doesn't surface.</summary>
    private RequestItem? _loadedItem;

    /// <summary>Builds a RequestItem from the current VM state, preserving fields not editable in the UI yet.</summary>
    public RequestItem BuildRequestItemFromVm()
    {
        var seed = _loadedItem ?? new RequestItem();
        return seed with
        {
            Method = Method,
            Url = Url,
            // IsBlank filters drop the auto-appended ghost row (and any manually blanked
            // rows) so placeholder chrome never reaches the .bru on disk.
            Headers = Headers.Where(h => !h.IsBlank).Select(h => new KvPair(h.Name, h.Value, h.Enabled)).ToList(),
            Params = Params.Where(p => !p.IsBlank).Select(p => new KvPair(p.Name, p.Value, p.Enabled)).ToList(),
            PreRequestVars = Variables.Where(v => !v.IsBlank).Select(v => new KvPair(v.Name, v.Value, v.Enabled)).ToList(),
            PostResponseVars = PostResponseVariables.Where(v => !v.IsBlank).Select(v => new KvPair(v.Name, v.Value, v.Enabled)).ToList(),
            Body = new BodyConfig
            {
                Mode = BodyType switch
                {
                    "json"            => BodyMode.Json,
                    "text"            => BodyMode.Text,
                    "xml"             => BodyMode.Xml,
                    "sparql"          => BodyMode.Sparql,
                    "graphql"         => BodyMode.GraphQL,
                    "form-urlencoded" => BodyMode.FormUrlEncoded,
                    "multipart-form"  => BodyMode.MultipartForm,
                    "file"            => BodyMode.Binary,
                    _                 => BodyMode.None,
                },
                Content          = string.IsNullOrEmpty(BodyContent) ? null : BodyContent,
                FormData         = FormUrlEncodedItems.Where(f => !f.IsBlank).Select(f => new KvPair(f.Name, f.Value, f.IsActive)).ToList(),
                MultipartItems   = MultipartItems.Select(m => new MultipartFormItem
                                   {
                                       Name = m.Name, Value = m.Value, Kind = m.Kind,
                                       ContentType = string.IsNullOrEmpty(m.ContentType) ? null : m.ContentType,
                                       Enabled = m.IsActive,
                                   }).ToList(),
                FilePath         = string.IsNullOrEmpty(FilePath) ? null : FilePath,
                FileContentType  = string.IsNullOrEmpty(FileContentType) ? null : FileContentType,
                GraphQLQuery     = string.IsNullOrEmpty(GraphQLQuery) ? null : GraphQLQuery,
                GraphQLVariables = string.IsNullOrEmpty(GraphQLVariables) ? null : GraphQLVariables,
            },
            Auth = BuildAuthConfig(),
            PreRequestScript = string.IsNullOrEmpty(PreRequestScript) ? null : PreRequestScript,
            PostResponseScript = string.IsNullOrEmpty(PostResponseScript) ? null : PostResponseScript,
            Tests = string.IsNullOrEmpty(TestsScript) ? null : TestsScript,
            Docs  = string.IsNullOrEmpty(Docs) ? null : Docs,
            Settings = new RequestSettingsConfig
            {
                FollowRedirects = SettingFollowRedirects,
                VerifySsl       = SettingVerifySsl,
                EncodeUrl       = SettingEncodeUrl,
                SendCookies     = SettingSendCookies,
                SaveCookies     = SettingSaveCookies,
                EnableHttp2     = SettingHttp2,
            },
            Soap = BuildSoapConfig(),
        };
    }

    // ============================== Composition helpers ==============================

    /// <summary>Append enabled, named query parameters as ?k=v&amp;k=v to the URL, after interpolation.</summary>
    public static string? ComposeUrl(string baseUrl, IEnumerable<KvEntry> queryParams,
        IReadOnlyDictionary<string, string>? vars = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var resolvedBase = vars is null ? baseUrl : Interpolator.Resolve(baseUrl, vars);

        var enabled = queryParams.Where(p => p.IsActive).ToList();
        if (enabled.Count == 0) return resolvedBase;

        var separator = resolvedBase.Contains('?') ? "&" : "?";
        var qs = string.Join("&", enabled.Select(p =>
        {
            var n = vars is null ? p.Name : Interpolator.Resolve(p.Name, vars);
            var v = vars is null ? p.Value : Interpolator.Resolve(p.Value, vars);
            return $"{Uri.EscapeDataString(n)}={Uri.EscapeDataString(v)}";
        }));
        return resolvedBase + separator + qs;
    }

    public static List<KeyValuePair<string, string>> ComposeHeaders(IEnumerable<KvEntry> headers,
        IReadOnlyDictionary<string, string>? vars = null) =>
        headers.Where(h => h.IsActive)
               .Select(h => new KeyValuePair<string, string>(
                   vars is null ? h.Name  : Interpolator.Resolve(h.Name, vars),
                   vars is null ? h.Value : Interpolator.Resolve(h.Value, vars)))
               .ToList();

    public static (string? Body, string? ContentType) ComposeBody(string bodyType, string content,
        IReadOnlyDictionary<string, string>? vars = null)
    {
        if (string.IsNullOrEmpty(content)) return (null, null);
        var resolved = vars is null ? content : Interpolator.Resolve(content, vars);
        return bodyType switch
        {
            "json"            => (resolved, "application/json"),
            "xml"             => (resolved, "application/xml"),
            "text"            => (resolved, "text/plain"),
            "sparql"          => (resolved, "application/sparql-query"),
            "form-urlencoded" => (resolved, "application/x-www-form-urlencoded"),
            "multipart-form"  => (resolved, "multipart/form-data"),
            "file"            => (resolved, "application/octet-stream"),
            _                 => (null, null),
        };
    }

    /// <summary>Compose a GraphQL JSON body from query + variables (both optionally interpolated).</summary>
    public static (string? Body, string? ContentType) ComposeGraphQLBody(
        string query, string variables, IReadOnlyDictionary<string, string>? vars = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return (null, null);

        var resolvedQuery = vars is null ? query : Interpolator.Resolve(query, vars);
        var resolvedVarsText = string.IsNullOrWhiteSpace(variables)
            ? "{}"
            : (vars is null ? variables : Interpolator.Resolve(variables, vars));

        // Validate variables are JSON-parseable; if not, send as raw string property.
        // Bruno just embeds it verbatim; we do the same so users see exactly what they typed.
        // The server is responsible for rejecting malformed variables.
        var json =
            "{\"query\":" + System.Text.Json.JsonSerializer.Serialize(resolvedQuery) +
            ",\"variables\":" + (LooksLikeJson(resolvedVarsText) ? resolvedVarsText : "{}") +
            "}";
        return (json, "application/json");
    }

    private static bool LooksLikeJson(string s)
    {
        var trimmed = s.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    /// <summary>Reads <c>WWW-Authenticate</c> from a 401, parses the Digest challenge,
    /// and constructs the <c>Authorization</c> value for the retry. Returns null if no
    /// digest challenge is present or required digest fields are missing.</summary>
    private string? TryBuildDigestRetry(HttpExecutionResult challengeResponse, Uri uri)
    {
        if (string.IsNullOrEmpty(DigestUsername)) return null;

        // RFC 7235 allows multiple WWW-Authenticate headers; iterate until we find a Digest one.
        foreach (var (name, value) in challengeResponse.Headers)
        {
            if (!string.Equals(name, "WWW-Authenticate", StringComparison.OrdinalIgnoreCase)) continue;
            // A single header may comma-separate schemes (e.g., "Digest ..., Basic ..."); split on the
            // first non-quoted comma followed by an alpha token would be hard, but in practice servers
            // either send separate headers or just Digest. Try each header value as-is first.
            if (DigestAuthenticator.TryParseChallenge(value, out var challenge) && challenge is not null)
            {
                var digestUri = uri.PathAndQuery;
                var built = DigestAuthenticator.BuildAuthorizationHeader(
                    challenge, Method, digestUri, DigestUsername, DigestPassword);
                return built.Value;
            }
        }
        return null;
    }

    /// <summary>Builds a flat dict from enabled, named variable rows. Last-wins for duplicates.</summary>
    public static Dictionary<string, string> BuildVariableLookup(IEnumerable<KvEntry> vars)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in vars)
        {
            if (!v.IsActive) continue;
            dict[v.Name] = v.Value;
        }
        return dict;
    }

    /// <summary>Merges multiple variable bags. Later sources override earlier ones.</summary>
    public static Dictionary<string, string> MergeVariables(
        params IReadOnlyDictionary<string, string>[] sources)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var src in sources)
        {
            foreach (var kv in src) merged[kv.Key] = kv.Value;
        }
        return merged;
    }

    /// <summary>Snapshot of currently-resolvable variables: env (lowest precedence) + request vars (highest).
    /// Drives URL/body variable highlighting, hover tooltips, and the autocomplete popup.</summary>
    public IReadOnlyDictionary<string, string> ResolveCurrentVariables() =>
        MergeVariables(EnvironmentVariables, BuildVariableLookup(Variables));

    /// <summary>Bindable mirror of <see cref="ResolveCurrentVariables"/> for AXAML controls.
    /// Refreshed via <see cref="VariablesSnapshotChanged"/>; the property notifies on that event
    /// so script editors re-render their {{var}} highlighting + autocomplete.</summary>
    public IReadOnlyDictionary<string, string> ResolvedVariablesSnapshot
    {
        get
        {
            // Subscribe lazily so we re-raise PropertyChanged when the snapshot rebuilds.
            if (!_snapshotSubscribed)
            {
                _snapshotSubscribed = true;
                VariablesSnapshotChanged += (_, _) => OnPropertyChanged(nameof(ResolvedVariablesSnapshot));
            }
            return ResolveCurrentVariables();
        }
    }
    private bool _snapshotSubscribed;

    /// <summary>Builds the AuthConfig that AuthApplier consumes from the per-type observable properties.</summary>
    /// <summary>Builds an AuthConfig that the AuthApplier turns into the right wire format
    /// for the acquired OAuth2 token, honoring the panel's "Add token to" + "Header Prefix"
    /// selections. Defaults (headers + "Bearer") produce the classic
    /// <c>Authorization: Bearer &lt;token&gt;</c> header.</summary>
    private AuthConfig BuildOAuth2BearerAuth(string accessToken)
    {
        // "headers" → Authorization: <prefix> <token>. Custom prefix supports the few IdPs
        // that demand JWT / Token / empty instead of Bearer.
        if (string.Equals(OAuth2AddTokenTo, "headers", StringComparison.OrdinalIgnoreCase))
        {
            // Bearer auth applier writes "Authorization: Bearer <token>". If the panel set
            // a different prefix, encode it as an ApiKey-on-header so the applier emits the
            // raw "Authorization: <prefix> <token>" form rather than re-prefixing Bearer.
            if (string.Equals(OAuth2HeaderPrefix, "Bearer", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(OAuth2HeaderPrefix))
            {
                return new AuthConfig
                {
                    Type = DomainAuthType.Bearer,
                    Parameters = new Dictionary<string, string> { ["token"] = accessToken },
                };
            }
            // Custom prefix path: deliver as an API-key header so the AuthApplier writes
            // the value verbatim without prepending "Bearer ".
            return new AuthConfig
            {
                Type = DomainAuthType.ApiKey,
                Parameters = new Dictionary<string, string>
                {
                    ["key"] = "Authorization",
                    ["value"] = string.IsNullOrEmpty(OAuth2HeaderPrefix)
                        ? accessToken
                        : OAuth2HeaderPrefix + " " + accessToken,
                    ["placement"] = "header",
                },
            };
        }

        // "queryparams" → token=<token> on the URL.
        if (string.Equals(OAuth2AddTokenTo, "queryparams", StringComparison.OrdinalIgnoreCase))
        {
            return new AuthConfig
            {
                Type = DomainAuthType.ApiKey,
                Parameters = new Dictionary<string, string>
                {
                    ["key"] = "access_token",
                    ["value"] = accessToken,
                    ["placement"] = "queryparams",
                },
            };
        }

        // "body" is rarely useful for outgoing requests, but support it for parity — fall
        // through to the Authorization header (same as default).
        return new AuthConfig
        {
            Type = DomainAuthType.Bearer,
            Parameters = new Dictionary<string, string> { ["token"] = accessToken },
        };
    }

    /// <summary>JSON-serializes Additional Parameters rows for storage inside the flat
    /// AuthConfig.Parameters dictionary. Empty list serializes to "[]" — survives a load
    /// without surprise. Used by both Token and Refresh sections (same shape).</summary>
    internal static string SerializeAdditionalParams(IEnumerable<OAuth2AdditionalParameter> rows)
    {
        // Ghost (blank) rows are UI chrome — never persist them.
        var list = rows.Where(r => !r.IsBlank)
                       .Select(r => new { key = r.Key, value = r.Value, sendIn = r.SendIn, enabled = r.IsActive });
        return System.Text.Json.JsonSerializer.Serialize(list);
    }

    /// <summary>Inverse of <see cref="SerializeAdditionalParams"/>. Tolerant of missing
    /// fields and bad JSON — returns an empty enumerable instead of throwing so a single
    /// malformed entry can't corrupt the auth panel.</summary>
    internal static IEnumerable<OAuth2AdditionalParameter> DeserializeAdditionalParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        System.Text.Json.JsonDocument? doc;
        try { doc = System.Text.Json.JsonDocument.Parse(json); }
        catch { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) yield break;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                yield return new OAuth2AdditionalParameter
                {
                    Key = el.TryGetProperty("key", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String ? (k.GetString() ?? string.Empty) : string.Empty,
                    Value = el.TryGetProperty("value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? string.Empty) : string.Empty,
                    SendIn = el.TryGetProperty("sendIn", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(s.GetString()) ? s.GetString()! : "body",
                    IsActive = !el.TryGetProperty("enabled", out var e) || e.ValueKind != System.Text.Json.JsonValueKind.False,
                };
            }
        }
    }

    public AuthConfig? BuildAuthConfig() => AuthType switch
    {
        "none"    => null,
        "inherit" => new AuthConfig { Type = DomainAuthType.Inherit },
        "bearer"  => new AuthConfig
        {
            Type = DomainAuthType.Bearer,
            Parameters = new Dictionary<string, string> { ["token"] = BearerToken }
        },
        "basic"   => new AuthConfig
        {
            Type = DomainAuthType.Basic,
            Parameters = new Dictionary<string, string>
            {
                ["username"] = BasicUsername,
                ["password"] = BasicPassword
            }
        },
        "digest"  => new AuthConfig
        {
            Type = DomainAuthType.Digest,
            Parameters = new Dictionary<string, string>
            {
                ["username"] = DigestUsername,
                ["password"] = DigestPassword
            }
        },
        "ntlm"    => new AuthConfig
        {
            Type = DomainAuthType.Ntlm,
            Parameters = new Dictionary<string, string>
            {
                ["username"] = NtlmUsername,
                ["password"] = NtlmPassword,
                ["domain"]   = NtlmDomain,
            }
        },
        "oauth1"  => new AuthConfig
        {
            Type = DomainAuthType.OAuth1,
            Parameters = new Dictionary<string, string>
            {
                ["consumerKey"]      = OAuth1ConsumerKey,
                ["consumerSecret"]   = OAuth1ConsumerSecret,
                ["token"]            = OAuth1Token,
                ["tokenSecret"]      = OAuth1TokenSecret,
                ["signatureMethod"]  = OAuth1SignatureMethod,
                ["realm"]            = OAuth1Realm,
            }
        },
        "wsse"    => new AuthConfig
        {
            Type = DomainAuthType.Wsse,
            Parameters = new Dictionary<string, string>
            {
                ["username"] = WsseUsername,
                ["password"] = WssePassword,
            }
        },
        "apikey"  => new AuthConfig
        {
            Type = DomainAuthType.ApiKey,
            Parameters = new Dictionary<string, string>
            {
                ["key"] = ApiKeyName,
                ["value"] = ApiKeyValue,
                ["placement"] = ApiKeyPlacement
            }
        },
        "oauth2"  => new AuthConfig
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
                // Bruno-parity additions — see RequestEditorViewModel for the property docs.
                ["refresh_token_url"] = OAuth2RefreshTokenUrl,
                ["token_source"]      = OAuth2TokenSource,
                ["token_id"]          = OAuth2TokenId,
                ["add_token_to"]      = OAuth2AddTokenTo,
                ["header_prefix"]     = OAuth2HeaderPrefix,
                ["auto_fetch"]        = OAuth2AutoFetch ? "true" : "false",
                ["auto_refresh"]      = OAuth2AutoRefresh ? "true" : "false",
                // Additional parameters serialized as JSON so the flat IDictionary<string,string>
                // shape of AuthConfig.Parameters keeps backward-compat with every existing file
                // format (no schema bump). Empty arrays are stored as "[]" — round-tripped
                // through ApplyAuthConfig.
                ["additional_token_params"]   = SerializeAdditionalParams(OAuth2TokenParameters),
                ["additional_refresh_params"] = SerializeAdditionalParams(OAuth2RefreshParameters),
            }
        },
        "awsv4"   => new AuthConfig
        {
            Type = DomainAuthType.AwsV4,
            Parameters = new Dictionary<string, string>
            {
                ["accessKeyId"]     = AwsAccessKeyId,
                ["secretAccessKey"] = AwsSecretAccessKey,
                ["region"]          = AwsRegion,
                ["service"]         = AwsService,
                ["sessionToken"]    = AwsSessionToken,
            }
        },
        _ => null
    };

    /// <summary>Populate auth fields from a Domain.AuthConfig (used when loading a request from disk).</summary>
    public void ApplyAuthConfig(AuthConfig? config)
    {
        if (config is null)
        {
            AuthType = "none";
            return;
        }

        switch (config.Type)
        {
            case DomainAuthType.Inherit:
                AuthType = "inherit";
                break;
            case DomainAuthType.Bearer:
                AuthType = "bearer";
                BearerToken = config.Parameters.TryGetValue("token", out var t) ? t : string.Empty;
                break;
            case DomainAuthType.Basic:
                AuthType = "basic";
                BasicUsername = config.Parameters.TryGetValue("username", out var u) ? u : string.Empty;
                BasicPassword = config.Parameters.TryGetValue("password", out var p) ? p : string.Empty;
                break;
            case DomainAuthType.Digest:
                AuthType = "digest";
                DigestUsername = config.Parameters.TryGetValue("username", out var du) ? du : string.Empty;
                DigestPassword = config.Parameters.TryGetValue("password", out var dp) ? dp : string.Empty;
                break;
            case DomainAuthType.Ntlm:
                AuthType = "ntlm";
                NtlmUsername = config.Parameters.TryGetValue("username", out var nu) ? nu : string.Empty;
                NtlmPassword = config.Parameters.TryGetValue("password", out var np) ? np : string.Empty;
                NtlmDomain   = config.Parameters.TryGetValue("domain",   out var nd) ? nd : string.Empty;
                break;
            case DomainAuthType.OAuth1:
                AuthType = "oauth1";
                OAuth1ConsumerKey    = config.Parameters.TryGetValue("consumerKey",     out var o1ck) ? o1ck : string.Empty;
                OAuth1ConsumerSecret = config.Parameters.TryGetValue("consumerSecret",  out var o1cs) ? o1cs : string.Empty;
                OAuth1Token          = config.Parameters.TryGetValue("token",           out var o1t)  ? o1t  : string.Empty;
                OAuth1TokenSecret    = config.Parameters.TryGetValue("tokenSecret",     out var o1ts) ? o1ts : string.Empty;
                OAuth1SignatureMethod = config.Parameters.TryGetValue("signatureMethod", out var o1sm) && !string.IsNullOrEmpty(o1sm) ? o1sm : "HMAC-SHA1";
                OAuth1Realm          = config.Parameters.TryGetValue("realm",           out var o1r)  ? o1r  : string.Empty;
                break;
            case DomainAuthType.Wsse:
                AuthType = "wsse";
                WsseUsername = config.Parameters.TryGetValue("username", out var wu) ? wu : string.Empty;
                WssePassword = config.Parameters.TryGetValue("password", out var wp) ? wp : string.Empty;
                break;
            case DomainAuthType.ApiKey:
                AuthType = "apikey";
                ApiKeyName = config.Parameters.TryGetValue("key", out var k) && !string.IsNullOrEmpty(k) ? k : "X-API-Key";
                ApiKeyValue = config.Parameters.TryGetValue("value", out var v) ? v : string.Empty;
                ApiKeyPlacement = config.Parameters.TryGetValue("placement", out var pl) && !string.IsNullOrEmpty(pl) ? pl : "header";
                break;
            case DomainAuthType.OAuth2:
                AuthType = "oauth2";
                OAuth2GrantType = config.Parameters.TryGetValue("grant_type", out var gt) && !string.IsNullOrEmpty(gt) ? gt : "client_credentials";
                OAuth2TokenUrl = config.Parameters.TryGetValue("access_token_url", out var tu) ? tu : string.Empty;
                OAuth2AuthorizationUrl = config.Parameters.TryGetValue("authorization_url", out var au) ? au : string.Empty;
                OAuth2CallbackUrl = config.Parameters.TryGetValue("callback_url", out var cb) && !string.IsNullOrEmpty(cb) ? cb : "http://127.0.0.1:8765/oauth/callback";
                OAuth2ClientId = config.Parameters.TryGetValue("client_id", out var ci) ? ci : string.Empty;
                OAuth2ClientSecret = config.Parameters.TryGetValue("client_secret", out var cs) ? cs : string.Empty;
                OAuth2Scope = config.Parameters.TryGetValue("scope", out var sc) ? sc : string.Empty;
                OAuth2State = config.Parameters.TryGetValue("state", out var stt) ? stt : string.Empty;
                OAuth2UsePkce = !config.Parameters.TryGetValue("use_pkce", out var up) || !string.Equals(up, "false", StringComparison.OrdinalIgnoreCase);
                OAuth2Username = config.Parameters.TryGetValue("username", out var ouu) ? ouu : string.Empty;
                OAuth2Password = config.Parameters.TryGetValue("password", out var oup) ? oup : string.Empty;
                OAuth2CredentialsPlacement = config.Parameters.TryGetValue("credentials_placement", out var cp) && !string.IsNullOrEmpty(cp) ? cp : "body";
                // Bruno-parity additions. Defaults preserve current behavior when the field
                // is missing — older files load cleanly without surprise UI flips.
                OAuth2RefreshTokenUrl = config.Parameters.TryGetValue("refresh_token_url", out var rtu) ? rtu : string.Empty;
                OAuth2TokenSource = config.Parameters.TryGetValue("token_source", out var ts) && !string.IsNullOrEmpty(ts) ? ts : "access_token";
                OAuth2TokenId = config.Parameters.TryGetValue("token_id", out var tid) && !string.IsNullOrEmpty(tid) ? tid : "credentials";
                OAuth2AddTokenTo = config.Parameters.TryGetValue("add_token_to", out var att) && !string.IsNullOrEmpty(att) ? att : "headers";
                OAuth2HeaderPrefix = config.Parameters.TryGetValue("header_prefix", out var hp) ? hp : "Bearer";
                OAuth2AutoFetch = !config.Parameters.TryGetValue("auto_fetch", out var af) || !string.Equals(af, "false", StringComparison.OrdinalIgnoreCase);
                OAuth2AutoRefresh = config.Parameters.TryGetValue("auto_refresh", out var ar) && string.Equals(ar, "true", StringComparison.OrdinalIgnoreCase);
                OAuth2TokenParameters.Clear();
                config.Parameters.TryGetValue("additional_token_params", out var tokParamsJson);
                foreach (var addP in DeserializeAdditionalParams(tokParamsJson))
                    OAuth2TokenParameters.Add(addP);
                OAuth2RefreshParameters.Clear();
                config.Parameters.TryGetValue("additional_refresh_params", out var refParamsJson);
                foreach (var addP in DeserializeAdditionalParams(refParamsJson))
                    OAuth2RefreshParameters.Add(addP);
                break;
            case DomainAuthType.AwsV4:
                AuthType = "awsv4";
                AwsAccessKeyId     = config.Parameters.TryGetValue("accessKeyId", out var ak) ? ak : string.Empty;
                AwsSecretAccessKey = config.Parameters.TryGetValue("secretAccessKey", out var sk) ? sk : string.Empty;
                AwsRegion          = config.Parameters.TryGetValue("region", out var rg) ? rg : string.Empty;
                AwsService         = config.Parameters.TryGetValue("service", out var sv) ? sv : string.Empty;
                AwsSessionToken    = config.Parameters.TryGetValue("sessionToken", out var st) ? st : string.Empty;
                break;
            default:
                AuthType = "none";
                break;
        }
    }

    private void RebuildTimelinePhases(Vegha.Core.Requests.HttpExecutionTiming t)
    {
        TimelinePhases.Clear();
        // Sum drives the bar width; if total is zero, fall back to total elapsed.
        var sum = t.SumOfPhases;
        if (sum <= 0) sum = t.TotalMs;
        if (sum <= 0) return;

        TimelinePhases.Add(new TimelinePhase("DNS lookup",     t.DnsMs,     t.DnsMs     / sum, "#60A5FA"));
        TimelinePhases.Add(new TimelinePhase("TCP connect",    t.ConnectMs, t.ConnectMs / sum, "#34D399"));
        TimelinePhases.Add(new TimelinePhase("TLS handshake",  t.TlsMs,     t.TlsMs     / sum, "#A78BFA"));
        TimelinePhases.Add(new TimelinePhase("Time to first byte", t.TtfbMs,    t.TtfbMs    / sum, "#FBBF24"));
        TimelinePhases.Add(new TimelinePhase("Content download",   t.ContentMs, t.ContentMs / sum, "#F472B6"));
    }

    private static string BuildRawResponseText(HttpExecutionResult result)
    {
        if (result.IsTransportError) return result.ErrorMessage ?? string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.Append("HTTP/1.1 ").Append(result.StatusCode).Append(' ').AppendLine(result.ReasonPhrase);
        foreach (var (name, value) in result.Headers)
        {
            sb.Append(name).Append(": ").AppendLine(value);
        }
        sb.AppendLine();
        sb.Append(result.Body);
        return sb.ToString();
    }

    // ====================================================================
    // OAuth2 panel commands — Get Access Token / Clear Cache / Add Param
    // ====================================================================

    /// <summary>Acquires (or refreshes from cache) an OAuth2 access token using the current
    /// VM state, then surfaces it in <see cref="OAuth2LastAccessToken"/> + decodes the JWT
    /// payload into <see cref="OAuth2DecodedPayload"/>. Invoked by the "Get Access Token"
    /// button. Failures land in <see cref="OAuth2StatusMessage"/>.</summary>
    [RelayCommand]
    public async Task GetOAuth2AccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (AuthType != "oauth2")
        {
            OAuth2StatusMessage = "Auth type is not OAuth 2.0.";
            return;
        }

        var vars = ResolveCurrentVariables();
        var additionalToken = OAuth2TokenParameters
            .Where(p => p.IsActive && !string.IsNullOrEmpty(p.Key))
            .Select(p => new OAuth2AdditionalParam(p.Key, p.Value, p.SendIn))
            .ToList();

        OAuth2TokenResult token;
        try
        {
            token = OAuth2GrantType switch
            {
                "password" => await _oauth2.AcquirePasswordAsync(
                    new OAuth2PasswordConfig(
                        TokenUrl: OAuth2TokenUrl,
                        ClientId: OAuth2ClientId,
                        ClientSecret: OAuth2ClientSecret,
                        Username: OAuth2Username,
                        Password: OAuth2Password,
                        Scope: string.IsNullOrWhiteSpace(OAuth2Scope) ? null : OAuth2Scope,
                        CredentialsPlacement: OAuth2CredentialsPlacement,
                        AdditionalParameters: additionalToken,
                        TokenId: OAuth2TokenId,
                        TokenSource: OAuth2TokenSource),
                    vars, cancellationToken),
                "authorization_code" => await _oauth2.AcquireAuthorizationCodeAsync(
                    new OAuth2AuthorizationCodeConfig(
                        AuthorizationUrl: OAuth2AuthorizationUrl,
                        TokenUrl: OAuth2TokenUrl,
                        ClientId: OAuth2ClientId,
                        ClientSecret: OAuth2ClientSecret,
                        CallbackUrl: OAuth2CallbackUrl,
                        Scope: string.IsNullOrWhiteSpace(OAuth2Scope) ? null : OAuth2Scope,
                        State: string.IsNullOrWhiteSpace(OAuth2State) ? null : OAuth2State,
                        UsePkce: OAuth2UsePkce,
                        CredentialsPlacement: OAuth2CredentialsPlacement,
                        AdditionalParameters: additionalToken,
                        TokenId: OAuth2TokenId,
                        TokenSource: OAuth2TokenSource),
                    vars, cancellationToken),
                _ => await _oauth2.AcquireClientCredentialsAsync(
                    new OAuth2ClientCredentialsConfig(
                        TokenUrl: OAuth2TokenUrl,
                        ClientId: OAuth2ClientId,
                        ClientSecret: OAuth2ClientSecret,
                        Scope: string.IsNullOrWhiteSpace(OAuth2Scope) ? null : OAuth2Scope,
                        CredentialsPlacement: OAuth2CredentialsPlacement,
                        AdditionalParameters: additionalToken,
                        TokenId: OAuth2TokenId,
                        TokenSource: OAuth2TokenSource),
                    vars, cancellationToken)
            };
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

        OAuth2LastAccessToken = token.AccessToken!;
        OAuth2TokenType = string.IsNullOrEmpty(token.TokenType) ? "Bearer" : token.TokenType!;
        OAuth2DecodedPayload = JwtDecoder.PrettyPrintPayload(token.AccessToken!);
        OAuth2StatusMessage = token.FromCache ? "Fetched from cache." : "Fetched.";
    }

    /// <summary>Clears the OAuth2 token cache slot for the current configuration so the next
    /// fetch goes back to the wire. Wired to the "Clear Cache" button.</summary>
    [RelayCommand]
    public void ClearOAuth2Cache()
    {
        _oauth2.InvalidateCacheForTokenId(OAuth2TokenId);
        OAuth2LastAccessToken = string.Empty;
        OAuth2DecodedPayload = string.Empty;
        OAuth2StatusMessage = "Cache cleared.";
    }

    /// <summary>"+ Add Parameter" handler for the Token tab of Additional Parameters.</summary>
    [RelayCommand]
    public void AddOAuth2TokenParameter() => OAuth2TokenParameters.Add(new OAuth2AdditionalParameter());

    /// <summary>"+ Add Parameter" handler for the Refresh tab of Additional Parameters.</summary>
    [RelayCommand]
    public void AddOAuth2RefreshParameter() => OAuth2RefreshParameters.Add(new OAuth2AdditionalParameter());

    /// <summary>Row-X remove on Additional Parameters / Token tab.</summary>
    [RelayCommand]
    public void RemoveOAuth2TokenParameter(OAuth2AdditionalParameter? row)
    {
        if (row is not null) OAuth2TokenParameters.Remove(row);
    }

    /// <summary>Row-X remove on Additional Parameters / Refresh tab.</summary>
    [RelayCommand]
    public void RemoveOAuth2RefreshParameter(OAuth2AdditionalParameter? row)
    {
        if (row is not null) OAuth2RefreshParameters.Remove(row);
    }

    /// <summary>Eye-icon toggle for the Client Secret field. Inverts the bool;
    /// the XAML binds PasswordChar to it.</summary>
    [RelayCommand]
    public void ToggleOAuth2SecretVisibility() => OAuth2IsClientSecretVisible = !OAuth2IsClientSecretVisible;

    // ====================================================================
    // Body editor commands — multipart + form-urlencoded + file
    // ====================================================================

    /// <summary>"+ Text row" — adds an empty text part to the multipart-form table.</summary>
    [RelayCommand]
    public void AddMultipartTextItem() => MultipartItems.Add(new MultipartFormRow { Kind = "text" });

    /// <summary>"+ File row" — adds an empty file part to the multipart-form table.
    /// The view typically follows up by opening the file picker through
    /// <see cref="PickMultipartFileRequested"/>.</summary>
    [RelayCommand]
    public void AddMultipartFileItem() => MultipartItems.Add(new MultipartFormRow { Kind = "file" });

    /// <summary>Remove handler for a multipart row.</summary>
    [RelayCommand]
    public void RemoveMultipartItem(MultipartFormRow? row)
    {
        if (row is not null) MultipartItems.Remove(row);
    }

    /// <summary>Fired by the VM when the user clicks the "📁" picker on a multipart file
    /// row. The view's code-behind subscribes, opens the file dialog via
    /// IStorageProvider, then writes the chosen path back into <paramref name="row.Value"/>.</summary>
    public event Action<MultipartFormRow>? PickMultipartFileRequested;

    [RelayCommand]
    public void PickMultipartFile(MultipartFormRow? row)
    {
        if (row is null) return;
        // Force the row to "file" kind in case the user clicked picker on a text row.
        row.Kind = "file";
        PickMultipartFileRequested?.Invoke(row);
    }

    /// <summary>Fired when the user clicks the "Pick file" button on the File/Binary body.
    /// View handles the dialog, writes the chosen path into <see cref="FilePath"/>.</summary>
    public event Action? PickBodyFileRequested;

    [RelayCommand]
    public void PickBodyFile() => PickBodyFileRequested?.Invoke();

    /// <summary>Clears a previously-picked file body so the request reverts to no body.</summary>
    [RelayCommand]
    public void ClearBodyFile()
    {
        FilePath = string.Empty;
        FileContentType = string.Empty;
    }

    // ----- Form-URL-Encoded row commands -----

    [RelayCommand]
    public void AddFormUrlEncodedItem() => FormUrlEncodedItems.Add(new KvEntry());

    [RelayCommand]
    public void RemoveFormUrlEncodedItem(KvEntry? row)
    {
        if (row is not null) FormUrlEncodedItems.Remove(row);
    }
}

public sealed record HeaderRow(string Name, string Value);

/// <summary>One Set-Cookie row in the response Cookies subtab. We parse the standard
/// attributes (Path, Domain, Expires, Max-Age, Secure, HttpOnly, SameSite) so the table
/// can display them; the global cookie jar still owns the live state.</summary>
public sealed record ResponseCookieRow(
    string Name,
    string Value,
    string Domain,
    string? Path,
    DateTimeOffset? Expires,
    int? MaxAge,
    bool Secure,
    bool HttpOnly,
    string? SameSite)
{
    public static ResponseCookieRow? Parse(string setCookieValue, string fallbackDomain)
    {
        if (string.IsNullOrWhiteSpace(setCookieValue)) return null;
        var parts = setCookieValue.Split(';');
        if (parts.Length == 0) return null;

        var first = parts[0].Trim();
        var eq = first.IndexOf('=');
        if (eq <= 0) return null;
        var name = first[..eq].Trim();
        var value = first[(eq + 1)..].Trim();
        if (string.IsNullOrEmpty(name)) return null;

        string? path = null;
        string domain = fallbackDomain;
        DateTimeOffset? expires = null;
        int? maxAge = null;
        bool secure = false, httpOnly = false;
        string? sameSite = null;

        for (var i = 1; i < parts.Length; i++)
        {
            var attr = parts[i].Trim();
            if (string.IsNullOrEmpty(attr)) continue;
            var eqIdx = attr.IndexOf('=');
            var key = (eqIdx < 0 ? attr : attr[..eqIdx]).Trim();
            var val = eqIdx < 0 ? null : attr[(eqIdx + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "path": path = val; break;
                case "domain": if (!string.IsNullOrEmpty(val)) domain = val!; break;
                case "expires":
                    if (DateTimeOffset.TryParse(val, out var e)) expires = e;
                    break;
                case "max-age":
                    if (int.TryParse(val, out var ma)) maxAge = ma;
                    break;
                case "secure": secure = true; break;
                case "httponly": httpOnly = true; break;
                case "samesite": sameSite = val; break;
            }
        }

        return new ResponseCookieRow(name, value, domain, path, expires, maxAge, secure, httpOnly, sameSite);
    }

    public string FlagsLabel
    {
        get
        {
            var flags = new List<string>();
            if (Secure) flags.Add("Secure");
            if (HttpOnly) flags.Add("HttpOnly");
            if (!string.IsNullOrEmpty(SameSite)) flags.Add("SameSite=" + SameSite);
            return string.Join(" · ", flags);
        }
    }
}

public sealed record TestResultRow(string Name, bool Passed, string? FailureMessage, double DurationMs);

/// <summary>One console line in the run-output panel.</summary>
public sealed record ConsoleLineRow(string Level, string Text);

/// <summary>One variable row in the run-output panel, grouped by <paramref name="Scope"/>.</summary>
public sealed record InspectVarRow(string Scope, string Name, string Value);

/// <summary>One row in the Timeline subtab. <c>WidthRatio</c> is the phase's share of the total bar width (0..1).</summary>
public sealed record TimelinePhase(string Name, double DurationMs, double WidthRatio, string ColorHex);

/// <summary>
/// A user-defined extra parameter attached to OAuth2 token (or refresh_token) requests.
/// Bruno's "Additional Parameters → Token / Refresh" rows. <see cref="SendIn"/> picks the
/// destination on the wire: "body" (default — form-encoded with the grant params),
/// "headers" (added to the token request's HTTP headers), or "queryparams" (appended to
/// the token URL). Variable references in Key / Value are interpolated at fetch time.
/// </summary>
public partial class OAuth2AdditionalParameter : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    private string _key = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    private string _value = string.Empty;

    [ObservableProperty] private string _sendIn = "body";
    [ObservableProperty] private bool _isActive = true;

    /// <summary>True for the auto-appended placeholder row (both cells empty) — see
    /// <see cref="KvEntry.IsBlank"/>.</summary>
    public bool IsBlank => string.IsNullOrEmpty(Key) && string.IsNullOrEmpty(Value);
}

/// <summary>
/// A row in the multipart-form body editor. <see cref="Kind"/> distinguishes text rows
/// (Value is the literal field value) from file rows (Value is a file path picked via
/// the body editor's "📁" button). <see cref="ContentType"/> overrides the per-part
/// auto-detected MIME (null/empty falls back to the file extension's guess or
/// <c>text/plain</c> for text rows).
/// </summary>
public partial class MultipartFormRow : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
    /// <summary>"text" (default) or "file".</summary>
    [ObservableProperty] private string _kind = "text";
    [ObservableProperty] private string _contentType = string.Empty;
    [ObservableProperty] private bool _isActive = true;
}
