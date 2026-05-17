using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vegha.Integrations.Secrets;

namespace Vegha.Integrations.Secrets.Doppler;

/// <summary>
/// Doppler REST adapter. The token (service token or personal token) gets passed as
/// HTTP Basic auth's username with an empty password, per Doppler's API convention.
/// Path syntax: "project/config/SECRET_NAME". Field selector is unused.
/// </summary>
public sealed class DopplerProvider : ISecretProvider
{
    private readonly HttpClient _http;
    private readonly string _token;

    public string Name => "doppler";

    public DopplerProvider(string token, HttpClient? http = null)
    {
        _token = token;
        _http = http ?? new HttpClient { BaseAddress = new Uri("https://api.doppler.com/") };
    }

    public async Task<string?> GetSecretAsync(string path, string? field, CancellationToken cancellationToken = default)
    {
        var parts = path.Split('/');
        if (parts.Length < 3) return null;
        var project = parts[0];
        var config = parts[1];
        var name = parts[2];

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"v3/configs/config/secret?project={Uri.EscapeDataString(project)}" +
            $"&config={Uri.EscapeDataString(config)}" +
            $"&name={Uri.EscapeDataString(name)}");
        var auth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(_token + ":"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException(
            $"Doppler returned {(int)resp.StatusCode}");

        var body = await resp.Content.ReadFromJsonAsync<DopplerSecretResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return body?.Value?.Computed;
    }

    private sealed class DopplerSecretResponse
    {
        public string? Name { get; set; }
        public DopplerSecretValue? Value { get; set; }
    }

    private sealed class DopplerSecretValue
    {
        public string? Raw { get; set; }
        public string? Computed { get; set; }
    }
}
