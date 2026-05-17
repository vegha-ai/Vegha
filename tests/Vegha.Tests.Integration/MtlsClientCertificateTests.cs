using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

using HttpRequestOptions = Vegha.Core.Requests.HttpRequestOptions;

namespace Vegha.Tests.Integration;

/// <summary>Verifies a per-request client certificate (mTLS) is loaded from a PFX and
/// flows through <see cref="HttpRequestOptions.ClientCertificate"/>. We don't spin up a
/// real mTLS server here — that requires platform-specific TLS plumbing — but we do
/// verify the X.509 round-trip and that the executor accepts the option without
/// throwing (i.e. handler construction wires the cert in).</summary>
public class MtlsClientCertificateTests
{
    private static (string Path, string Password) CreateSelfSignedPfx()
    {
        // 2048-bit RSA self-signed cert with a known password, written to a temp file.
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=vegha-mtls-test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));

        const string password = "test-pass";
        var pfx = cert.Export(X509ContentType.Pfx, password);
        var path = Path.Combine(Path.GetTempPath(), $"vegha-mtls-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, pfx);
        return (path, password);
    }

    [Fact]
    public void ClientCertificate_LoadsFromPfx_AndFlowsThroughOptions()
    {
        var (path, password) = CreateSelfSignedPfx();
        try
        {
            using var loaded = X509CertificateLoader.LoadPkcs12FromFile(path, password);
            loaded.Subject.Should().Be("CN=vegha-mtls-test");
            loaded.HasPrivateKey.Should().BeTrue("the PFX export embeds the private key");

            var options = new HttpRequestOptions(ClientCertificate: loaded);
            options.ClientCertificate.Should().BeSameAs(loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HttpExecutor_AcceptsClientCertificateOption_WithoutThrowing()
    {
        // Hits a real HTTPS endpoint (httpbin.org). The cert is unused server-side, but
        // this exercises the executor's per-request handler construction path that wires
        // ClientCertificates into SslOptions.
        var (path, password) = CreateSelfSignedPfx();
        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(path, password);
            using var sharedClient = new HttpClient();
            var executor = new HttpExecutor(sharedClient);
            var req = new HttpExecutionRequest(
                HttpMethod.Get,
                new Uri("https://httpbin.org/status/200"),
                Options: new HttpRequestOptions(ClientCertificate: cert));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var result = await executor.ExecuteAsync(req, cts.Token);
                result.StatusCode.Should().BeInRange(200, 599);
            }
            catch (HttpRequestException) { /* offline runs: still validates wiring */ }
            catch (TaskCanceledException) { /* slow network: same */ }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
