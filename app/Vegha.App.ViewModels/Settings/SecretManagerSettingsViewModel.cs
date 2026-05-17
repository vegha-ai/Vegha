using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Persistence;
using Vegha.Integrations.Secrets;

namespace Vegha.App.ViewModels.Settings;

/// <summary>Settings page for configuring external secret managers (Azure Key Vault,
/// AWS Secrets Manager). Each provider type declares the set of fields its adapter needs —
/// for Azure the Entra ID service-principal credentials (tenant / client / secret), for AWS
/// the IAM access keys. Configs are persisted — encrypted — via
/// <see cref="SecretProviderConfigStore"/> independently of <see cref="AppSettings"/>:
/// add / remove take effect immediately, so <see cref="ReadFrom"/> / <see cref="WriteTo"/>
/// are intentional no-ops (the Settings window's Save/Cancel don't govern this page).</summary>
public partial class SecretManagerSettingsViewModel : SettingsPageBase
{
    private readonly SecretProviderConfigStore _store;

    /// <summary>Builds a live provider adapter from a config — supplied by the app layer
    /// (the ViewModels assembly can't reference the concrete Azure/AWS adapters). Used by the
    /// per-row "Test" button to make a real call against the configured vault.</summary>
    private readonly Func<SecretProviderConfig, ISecretProvider?>? _providerFactory;

    public override string Id => "secrets";
    public override string Title => "Secret Manager";
    public override string IconKey => "Vault";

    /// <summary>Supported provider types and the configuration fields each adapter consumes.
    /// Secret fields (client secret, AWS secret access key, session token) are masked in the
    /// UI and encrypted at rest. Auth fields are optional: leaving the credential group blank
    /// falls back to ambient credentials (Azure CLI / AWS default credential chain).</summary>
    public IReadOnlyList<SecretProviderTypeOption> ProviderTypes { get; } = new[]
    {
        new SecretProviderTypeOption("azure", "Azure Key Vault", new[]
        {
            new SecretProviderFieldDef("vaultUri", "Vault URI",
                "https://my-vault.vault.azure.net/", IsSecret: false, Required: true),
            new SecretProviderFieldDef("tenantId", "Directory (tenant) ID",
                "00000000-0000-0000-0000-000000000000", IsSecret: false, Required: false),
            new SecretProviderFieldDef("clientId", "Application (client) ID",
                "00000000-0000-0000-0000-000000000000", IsSecret: false, Required: false),
            new SecretProviderFieldDef("clientSecret", "Client secret",
                "Entra ID app registration secret", IsSecret: true, Required: false),
        }),
        new SecretProviderTypeOption("aws", "AWS Secrets Manager", new[]
        {
            new SecretProviderFieldDef("region", "Region",
                "us-east-1", IsSecret: false, Required: true),
            new SecretProviderFieldDef("accessKeyId", "Access key ID",
                "AKIA...", IsSecret: false, Required: false),
            new SecretProviderFieldDef("secretAccessKey", "Secret access key",
                "IAM secret access key", IsSecret: true, Required: false),
            new SecretProviderFieldDef("sessionToken", "Session token (optional)",
                "For temporary STS / SSO credentials", IsSecret: true, Required: false),
        }),
    };

    public ObservableCollection<SecretProviderRow> Providers { get; } = new();

    /// <summary>Editable inputs for the "add provider" form — rebuilt whenever the selected
    /// provider type changes so the field set always matches the chosen adapter.</summary>
    public ObservableCollection<ProviderFieldInput> NewFields { get; } = new();

    [ObservableProperty] private SecretProviderTypeOption _selectedProviderType;
    [ObservableProperty] private string _newProviderName = string.Empty;
    [ObservableProperty] private string? _statusMessage;

    public SecretManagerSettingsViewModel(Func<SecretProviderConfig, ISecretProvider?>? providerFactory = null)
        : this(new SecretProviderConfigStore(), providerFactory) { }

    /// <summary>Test-friendly ctor: pass an explicit store.</summary>
    public SecretManagerSettingsViewModel(
        SecretProviderConfigStore store,
        Func<SecretProviderConfig, ISecretProvider?>? providerFactory = null)
    {
        _store = store;
        _providerFactory = providerFactory;
        _selectedProviderType = ProviderTypes[0];
        RebuildFields();
        ReloadProviders();
    }

    public bool HasProviders => Providers.Count > 0;

    partial void OnSelectedProviderTypeChanged(SecretProviderTypeOption value) => RebuildFields();

    private void RebuildFields()
    {
        NewFields.Clear();
        foreach (var def in SelectedProviderType.Fields)
            NewFields.Add(new ProviderFieldInput(def));
    }

    private void ReloadProviders()
    {
        Providers.Clear();
        foreach (var c in _store.Load())
            Providers.Add(new SecretProviderRow(c, _providerFactory));
        OnPropertyChanged(nameof(HasProviders));
    }

    [RelayCommand]
    private void AddProvider()
    {
        var name = NewProviderName.Trim();
        if (string.IsNullOrEmpty(name)) { StatusMessage = "Enter a name for the provider."; return; }

        foreach (var f in NewFields)
        {
            if (f.Required && string.IsNullOrWhiteSpace(f.Value))
            {
                StatusMessage = $"“{f.Label}” is required.";
                return;
            }
        }

        var settings = NewFields
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .ToDictionary(f => f.Key, f => f.Value.Trim());

        var credentialError = ValidateCredentialGroup(SelectedProviderType.Id, settings);
        if (credentialError is not null) { StatusMessage = credentialError; return; }

        var configs = _store.Load().ToList();
        if (configs.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"A provider named “{name}” already exists.";
            return;
        }

        configs.Add(new SecretProviderConfig(name, SelectedProviderType.Id, settings));
        _store.Save(configs);

        ReloadProviders();
        NewProviderName = string.Empty;
        RebuildFields();
        StatusMessage = $"Added “{name}”.";
    }

    /// <summary>Credential fields come in all-or-nothing groups: a partially-filled service
    /// principal / IAM key pair is a misconfiguration rather than a valid fallback.</summary>
    private static string? ValidateCredentialGroup(string typeId, IReadOnlyDictionary<string, string> settings)
    {
        bool Has(string key) => settings.ContainsKey(key);

        if (typeId == "azure")
        {
            var any = Has("tenantId") || Has("clientId") || Has("clientSecret");
            var all = Has("tenantId") && Has("clientId") && Has("clientSecret");
            if (any && !all)
                return "Provide tenant ID, client ID and client secret together — or leave all three blank to use Azure CLI / managed identity.";
        }
        else if (typeId == "aws")
        {
            if (Has("sessionToken") && !(Has("accessKeyId") && Has("secretAccessKey")))
                return "A session token also needs an access key ID and secret access key.";
            var any = Has("accessKeyId") || Has("secretAccessKey");
            var all = Has("accessKeyId") && Has("secretAccessKey");
            if (any && !all)
                return "Provide the access key ID and secret access key together — or leave both blank to use the AWS default credential chain.";
        }
        return null;
    }

    [RelayCommand]
    private void RemoveProvider(SecretProviderRow? row)
    {
        if (row is null) return;
        var configs = _store.Load()
            .Where(c => !string.Equals(c.Name, row.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _store.Save(configs);
        ReloadProviders();
        StatusMessage = $"Removed “{row.Name}”.";
    }

    // Provider configs persist immediately on add/remove — nothing to read from or write
    // into the AppSettings record.
    public override void ReadFrom(AppSettings settings) { }
    public override AppSettings WriteTo(AppSettings existing) => existing;
}

/// <summary>Declares one configuration field of a secret-manager provider type.</summary>
public sealed record SecretProviderFieldDef(
    string Key, string Label, string Example, bool IsSecret, bool Required);

/// <summary>One selectable secret-manager type in the "add provider" form.</summary>
public sealed record SecretProviderTypeOption(
    string Id, string DisplayName, IReadOnlyList<SecretProviderFieldDef> Fields);

/// <summary>An editable field value in the "add provider" form.</summary>
public partial class ProviderFieldInput : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public string Example { get; }
    public bool IsSecret { get; }
    public bool Required { get; }

    [ObservableProperty] private string _value = string.Empty;

    /// <summary>Drives the field's <c>PasswordChar</c> via <c>BoolToPasswordCharConverter</c>:
    /// non-secret fields render in plain text, secret fields are masked.</summary>
    public bool ShowPlain => !IsSecret;

    public ProviderFieldInput(SecretProviderFieldDef def)
    {
        Key = def.Key;
        Label = def.Label;
        Example = def.Example;
        IsSecret = def.IsSecret;
        Required = def.Required;
    }
}

/// <summary>A configured provider shown in the page's list. Only non-secret detail is
/// surfaced; credentials never appear. Carries a per-row "Test" action that makes a real
/// call against the vault and reports success or the underlying error.</summary>
public partial class SecretProviderRow : ObservableObject
{
    private readonly SecretProviderConfig _config;
    private readonly Func<SecretProviderConfig, ISecretProvider?>? _factory;

    public string Name => _config.Name;
    public string TypeLabel { get; }
    public string Detail { get; }

    /// <summary>Watermark for the test input — Azure looks up a secret name, AWS a secret ID.</summary>
    public string TestFieldHint { get; }

    [ObservableProperty] private string _testSecretName = string.Empty;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _isTesting;

    public SecretProviderRow(SecretProviderConfig config, Func<SecretProviderConfig, ISecretProvider?>? factory)
    {
        _config = config;
        _factory = factory;
        TypeLabel = config.Type switch
        {
            "azure" => "Azure Key Vault",
            "aws" => "AWS Secrets Manager",
            _ => config.Type,
        };
        Detail =
            config.Settings.TryGetValue("vaultUri", out var u) ? u :
            config.Settings.TryGetValue("region", out var r) ? r :
            string.Empty;
        TestFieldHint = config.Type == "aws"
            ? "a secret ID to fetch"
            : "a secret name to fetch";
    }

    /// <summary>Fetches one secret to verify the config end to end: it exercises auth, network
    /// and (for Azure) the vault access policy. The resolved value is never shown — only its
    /// length — so testing can't leak the secret.</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        var secretName = TestSecretName.Trim();
        if (secretName.Length == 0) { TestResult = "Enter a secret name to test."; return; }
        if (_factory is null) { TestResult = "Testing is unavailable in this context."; return; }

        IsTesting = true;
        TestResult = "Testing…";
        try
        {
            var provider = _factory(_config);
            if (provider is null)
            {
                TestResult = "✗ Provider configuration is invalid — check the required fields.";
                return;
            }

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var value = await provider.GetSecretAsync(secretName, field: null, cts.Token);
            TestResult = value is null
                ? "✗ Connected and authenticated, but no secret with that name was found."
                : $"✓ Success — resolved the secret ({value.Length} characters).";
        }
        catch (OperationCanceledException)
        {
            TestResult = "✗ Timed out after 30s — check the vault URI and network connectivity.";
        }
        catch (Exception ex)
        {
            TestResult = "✗ " + ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }
}
