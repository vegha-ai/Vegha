using Amazon;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Vegha.Integrations.Secrets;

namespace Vegha.Integrations.Secrets.Aws;

/// <summary>
/// AWS Secrets Manager adapter. AWS does not use OAuth — requests are signed with
/// Signature V4 using AWS credentials. Two credential modes are supported, selected by
/// <see cref="BuildCredentials"/>:
/// <list type="bullet">
///   <item><description><b>Static IAM keys</b> — when <c>accessKeyId</c> + <c>secretAccessKey</c>
///   are configured. Adding a <c>sessionToken</c> switches to temporary (STS / SSO /
///   AssumeRole) credentials.</description></item>
///   <item><description><b>Ambient</b> — otherwise the SDK's default credential chain
///   (environment variables → shared <c>~/.aws/credentials</c> profile → SSO → EC2/ECS/Lambda
///   role).</description></item>
/// </list>
/// A <c>region</c> is always required — Secrets Manager is a regional service.
///
/// Path is the secret ID (name or ARN); <c>field</c> optionally selects a key out of a
/// JSON-shaped SecretString.
/// </summary>
public sealed class AwsSecretsProvider : ISecretProvider
{
    private readonly IAmazonSecretsManager _client;

    public string Name => "aws";

    public AwsSecretsProvider() : this(new AmazonSecretsManagerClient()) { }
    public AwsSecretsProvider(string region) : this(new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region))) { }
    public AwsSecretsProvider(IAmazonSecretsManager client) { _client = client; }

    /// <summary>Builds a provider from a <c>SecretProviderConfig.Settings</c> dictionary.
    /// Recognised keys: <c>region</c> (required), <c>accessKeyId</c>, <c>secretAccessKey</c>,
    /// <c>sessionToken</c>.</summary>
    public static AwsSecretsProvider FromConfig(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("region", out var region) || string.IsNullOrWhiteSpace(region))
            throw new ArgumentException("AWS Secrets Manager configuration requires a 'region'.");

        var endpoint = RegionEndpoint.GetBySystemName(region.Trim());
        var credentials = BuildCredentials(settings);
        return new AwsSecretsProvider(credentials is null
            ? new AmazonSecretsManagerClient(endpoint)
            : new AmazonSecretsManagerClient(credentials, endpoint));
    }

    /// <summary>Returns static IAM credentials when an access key + secret are present
    /// (a session token upgrades them to temporary STS credentials), or <c>null</c> to
    /// signal "use the SDK's default credential chain".</summary>
    public static AWSCredentials? BuildCredentials(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue("accessKeyId", out var accessKeyId);
        settings.TryGetValue("secretAccessKey", out var secretAccessKey);
        settings.TryGetValue("sessionToken", out var sessionToken);

        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
            return null;

        return string.IsNullOrWhiteSpace(sessionToken)
            ? new BasicAWSCredentials(accessKeyId.Trim(), secretAccessKey.Trim())
            : new SessionAWSCredentials(accessKeyId.Trim(), secretAccessKey.Trim(), sessionToken.Trim());
    }

    public async Task<string?> GetSecretAsync(string path, string? field, CancellationToken cancellationToken = default)
    {
        try
        {
            var resp = await _client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = path }, cancellationToken)
                .ConfigureAwait(false);
            var raw = resp.SecretString;
            if (string.IsNullOrEmpty(raw)) return null;
            if (string.IsNullOrEmpty(field)) return raw;

            // SecretString is commonly JSON; pull the field if so. If it's plaintext, the
            // user shouldn't have specified a field — but we return null rather than guess.
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty(field, out var prop))
                    return prop.ValueKind == System.Text.Json.JsonValueKind.String ? prop.GetString() : prop.GetRawText();
            }
            catch { return null; }
            return null;
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }
}
