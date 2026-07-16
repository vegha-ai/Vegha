using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>Verifies the trust-list parser handles each of the supported entry forms:
/// inline PEM blocks, PEM files on disk, DER files on disk. Bad entries are skipped
/// so one typo doesn't kill the whole list.</summary>
public class CertificateLoaderTests
{
    private static (string Pem, X509Certificate2 Cert) MakeSelfSigned(string subject = "CN=vegha-test-ca")
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pem = "-----BEGIN CERTIFICATE-----\n"
            + Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END CERTIFICATE-----\n";
        return (pem, cert);
    }

    [Fact]
    public void Parse_InlinePem_LoadsCert()
    {
        var (pem, cert) = MakeSelfSigned();
        var collection = CertificateLoader.Parse(new[] { pem });
        collection.Count.Should().Be(1);
        collection[0].Subject.Should().Be(cert.Subject);
    }

    [Fact]
    public void Parse_PemFile_LoadsCert()
    {
        var (pem, _) = MakeSelfSigned();
        var path = Path.Combine(Path.GetTempPath(), $"vegha-trust-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, pem);
        try
        {
            var collection = CertificateLoader.Parse(new[] { path });
            collection.Count.Should().Be(1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Parse_DerFile_LoadsCert()
    {
        var (_, cert) = MakeSelfSigned();
        var path = Path.Combine(Path.GetTempPath(), $"vegha-trust-{Guid.NewGuid():N}.cer");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        try
        {
            var collection = CertificateLoader.Parse(new[] { path });
            collection.Count.Should().Be(1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Parse_BadEntries_AreSkipped_GoodOnesStillLoad()
    {
        var (pem, _) = MakeSelfSigned();
        var collection = CertificateLoader.Parse(new[]
        {
            "/does/not/exist.pem",
            "garbage that is not pem",
            string.Empty,
            pem,
        });
        collection.Count.Should().Be(1);
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmptyCollection()
    {
        CertificateLoader.Parse(null).Count.Should().Be(0);
        CertificateLoader.Parse(Array.Empty<string>()).Count.Should().Be(0);
    }

    [Fact]
    public void Parse_MultiBlockPem_LoadsAllCerts()
    {
        var (pem1, _) = MakeSelfSigned("CN=ca1");
        var (pem2, _) = MakeSelfSigned("CN=ca2");
        var bundle = pem1 + pem2;
        var collection = CertificateLoader.Parse(new[] { bundle });
        collection.Count.Should().Be(2);
    }

    // ==================== LoadClientCertificate (mTLS) ====================

    [Fact]
    public void LoadClientCertificate_Pkcs12WithPassword_LoadsWithPrivateKey()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=client-p12", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), $"vegha-cl-{Guid.NewGuid():N}.p12");
        File.WriteAllBytes(path, cert.Export(
            System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "pw"));
        try
        {
            using var loaded = CertificateLoader.LoadClientCertificate(path, "pw");
            loaded.Subject.Should().Be("CN=client-p12");
            loaded.HasPrivateKey.Should().BeTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadClientCertificate_PemCertAndKey_LoadsWithPrivateKey()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=client-pem", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var pem = cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem();
        var path = Path.Combine(Path.GetTempPath(), $"vegha-cl-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, pem);
        try
        {
            using var loaded = CertificateLoader.LoadClientCertificate(path);
            loaded.Subject.Should().Be("CN=client-pem");
            loaded.HasPrivateKey.Should().BeTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadClientCertificate_MissingFile_Throws()
    {
        Action act = () => CertificateLoader.LoadClientCertificate("/nope/absent.p12", "pw");
        act.Should().Throw<FileNotFoundException>();
    }
}
