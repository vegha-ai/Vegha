using System.Security.Cryptography.X509Certificates;

namespace Vegha.Core.Requests;

/// <summary>
/// Parses the user's "Trusted CAs" entries (from Settings) into an
/// <see cref="X509Certificate2Collection"/> the executor can install on
/// <c>SslOptions.CertificateChainPolicy.CustomTrustStore</c>.
///
/// Each entry is either:
///   - An absolute file path to a PEM bundle (one or more BEGIN CERTIFICATE blocks)
///   - An inline PEM block (<c>-----BEGIN CERTIFICATE-----…-----END CERTIFICATE-----</c>)
///   - A DER-encoded .crt / .cer path
///
/// Bad entries are silently skipped — the user sees the failed roots in a future
/// "validation report" but the rest of the list still applies.
/// </summary>
public static class CertificateLoader
{
    public static X509Certificate2Collection Parse(IEnumerable<string>? entries)
    {
        var collection = new X509Certificate2Collection();
        if (entries is null) return collection;

        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();

            try
            {
                if (trimmed.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
                {
                    LoadPemFromString(trimmed, collection);
                }
                else if (File.Exists(trimmed))
                {
                    LoadFromFile(trimmed, collection);
                }
                // Anything else — silently skip rather than throw on a typo.
            }
            catch
            {
                /* tolerate per-entry failures so one bad cert doesn't kill the whole list */
            }
        }
        return collection;
    }

    private static void LoadFromFile(string path, X509Certificate2Collection sink)
    {
        // Try as PEM first (works for .pem, .crt that's PEM-encoded, .cer text).
        var bytes = File.ReadAllBytes(path);
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        if (asText.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
        {
            LoadPemFromString(asText, sink);
            return;
        }
        // Fall back to DER. X509CertificateLoader is the supported entry point
        // since .NET 9; the X509Certificate2(byte[]) constructor is obsolete (SYSLIB0057).
        sink.Add(X509CertificateLoader.LoadCertificate(bytes));
    }

    private static void LoadPemFromString(string pem, X509Certificate2Collection sink)
    {
        // X509Certificate2Collection.ImportFromPem handles multi-block PEM bundles.
        sink.ImportFromPem(pem);
    }
}
