using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vegha.Integrations.Secrets;

namespace Vegha.Integrations.Secrets.OnePassword;

/// <summary>
/// 1Password Connect REST adapter. Path syntax: "vault-id/item-id" (or vault-name /
/// item-name — Connect resolves both). Field selector picks a specific field on the
/// item; default is the first password-type field's value.
///
/// Connect is the self-hosted server option; the cloud-only "Service Account" API
/// follows the same shapes if a user routes through that instead.
/// </summary>
public sealed class OnePasswordConnectProvider : ISecretProvider
{
    private readonly HttpClient _http;
    private readonly string _token;

    public string Name => "1password";

    public OnePasswordConnectProvider(string baseUrl, string token, HttpClient? http = null)
    {
        _token = token;
        _http = http ?? new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    public async Task<string?> GetSecretAsync(string path, string? field, CancellationToken cancellationToken = default)
    {
        var parts = path.Split('/');
        if (parts.Length < 2) return null;
        var vault = parts[0];
        var item = parts[1];

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"v1/vaults/{Uri.EscapeDataString(vault)}/items/{Uri.EscapeDataString(item)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"1Password returned {(int)resp.StatusCode}");

        var body = await resp.Content.ReadFromJsonAsync<OnePassItem>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (body?.Fields is null) return null;

        if (!string.IsNullOrEmpty(field))
            return body.Fields.FirstOrDefault(f => f.Label == field || f.Id == field)?.Value;

        // Default: first password-type field (the common case).
        var pw = body.Fields.FirstOrDefault(f => string.Equals(f.Type, "CONCEALED", StringComparison.OrdinalIgnoreCase));
        return pw?.Value ?? body.Fields.FirstOrDefault()?.Value;
    }

    private sealed class OnePassItem
    {
        public string? Id { get; set; }
        public List<OnePassField>? Fields { get; set; }
    }

    private sealed class OnePassField
    {
        public string? Id { get; set; }
        public string? Label { get; set; }
        public string? Type { get; set; }
        public string? Value { get; set; }
    }
}
