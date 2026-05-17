using Google.Cloud.SecretManager.V1;
using Vegha.Integrations.Secrets;

namespace Vegha.Integrations.Secrets.Gcp;

/// <summary>
/// GCP Secret Manager adapter. Uses the SDK's default credential chain (GOOGLE_APPLICATION_CREDENTIALS,
/// gcloud CLI, metadata server). Path syntax mirrors GCP's resource form
/// "projects/PROJECT/secrets/NAME/versions/latest" or just "PROJECT/NAME" when the user
/// doesn't care about a specific version.
///
/// Field selector is a no-op — GCP secrets are scalar bytes/strings.
/// </summary>
public sealed class GcpSecretManagerProvider : ISecretProvider
{
    private readonly SecretManagerServiceClient _client;

    public string Name => "gcp";

    public GcpSecretManagerProvider() : this(SecretManagerServiceClient.Create()) { }
    public GcpSecretManagerProvider(SecretManagerServiceClient client) { _client = client; }

    public async Task<string?> GetSecretAsync(string path, string? field, CancellationToken cancellationToken = default)
    {
        try
        {
            var resourceName = NormalizeResource(path);
            var resp = await _client.AccessSecretVersionAsync(resourceName, cancellationToken).ConfigureAwait(false);
            return resp.Payload.Data.ToStringUtf8();
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Accepts both "projects/X/secrets/Y/versions/latest" and short "X/Y" forms.</summary>
    private static string NormalizeResource(string path)
    {
        if (path.StartsWith("projects/", StringComparison.Ordinal)) return path;
        var parts = path.Split('/');
        if (parts.Length == 2) return $"projects/{parts[0]}/secrets/{parts[1]}/versions/latest";
        return path;
    }
}
