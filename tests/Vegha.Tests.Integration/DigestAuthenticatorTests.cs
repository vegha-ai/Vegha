using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

public class DigestAuthenticatorTests
{
    /// <summary>Test seam: feed a fixed cnonce so we can match RFC fixture vectors exactly.</summary>
    private sealed class FixedCnonce : DigestAuthenticator.ICnonceProvider
    {
        private readonly string _value;
        public FixedCnonce(string value) { _value = value; }
        public string NewCnonce() => _value;
    }

    [Fact]
    public void Md5_Auth_RFC2617_Example_ProducesExpectedResponse()
    {
        // From RFC 2617 §3.5 — the canonical Mufasa/Circle Of Life worked example.
        var challenge = new DigestChallenge(
            Realm: "testrealm@host.com",
            Nonce: "dcd98b7102dd2f0e8b11d0f600bfb0c093",
            Algorithm: "MD5",
            Qop: "auth",
            Opaque: "5ccc069c403ebaf9f0171e9517f40e41",
            Userhash: false);

        var result = DigestAuthenticator.BuildAuthorizationHeader(
            challenge,
            method: "GET",
            requestUri: "/dir/index.html",
            username: "Mufasa",
            password: "Circle Of Life",
            nonceCount: 1,
            cnonceProvider: new FixedCnonce("0a4f113b"));

        result.Value.Should().Contain("response=\"6629fae49393a05397450978507c4ef1\"");
        result.Value.Should().Contain("username=\"Mufasa\"");
        result.Value.Should().Contain("realm=\"testrealm@host.com\"");
        result.Value.Should().Contain("uri=\"/dir/index.html\"");
        result.Value.Should().Contain("nc=00000001");
        result.Value.Should().Contain("qop=auth");
        result.Value.Should().Contain("cnonce=\"0a4f113b\"");
        result.Value.Should().Contain("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"");
    }

    [Fact]
    public void Sha256_Auth_RFC7616_Example_ProducesExpectedResponse()
    {
        // From RFC 7616 §3.9.1 — same user/password but lowercase 'o' in "Circle of Life".
        var challenge = new DigestChallenge(
            Realm: "http-auth@example.org",
            Nonce: "7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v",
            Algorithm: "SHA-256",
            Qop: "auth, auth-int",
            Opaque: "FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS",
            Userhash: false);

        var result = DigestAuthenticator.BuildAuthorizationHeader(
            challenge,
            method: "GET",
            requestUri: "/dir/index.html",
            username: "Mufasa",
            password: "Circle of Life",
            nonceCount: 1,
            cnonceProvider: new FixedCnonce("f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ"));

        result.Value.Should().Contain("response=\"753927fa0e85d155564e2e272a28d1802ca10daf4496794697cf8db5856cb6c1\"");
        result.Value.Should().Contain("algorithm=SHA-256");
        // "auth" wins over "auth-int" when the server advertises both.
        result.Value.Should().Contain("qop=auth,");
    }

    [Fact]
    public void TryParseChallenge_ParsesQuotedAndUnquotedDirectives()
    {
        var ok = DigestAuthenticator.TryParseChallenge(
            "Digest realm=\"testrealm@host.com\", " +
            "qop=\"auth,auth-int\", " +
            "nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", " +
            "opaque=\"5ccc069c403ebaf9f0171e9517f40e41\", " +
            "algorithm=MD5",
            out var challenge);

        ok.Should().BeTrue();
        challenge.Should().NotBeNull();
        challenge!.Realm.Should().Be("testrealm@host.com");
        challenge.Nonce.Should().Be("dcd98b7102dd2f0e8b11d0f600bfb0c093");
        challenge.Algorithm.Should().Be("MD5");
        challenge.Qop.Should().Be("auth,auth-int");
        challenge.Opaque.Should().Be("5ccc069c403ebaf9f0171e9517f40e41");
        challenge.Userhash.Should().BeFalse();
    }

    [Fact]
    public void TryParseChallenge_ReturnsFalse_OnNonDigestScheme()
    {
        var ok = DigestAuthenticator.TryParseChallenge("Basic realm=\"foo\"", out var challenge);
        ok.Should().BeFalse();
        challenge.Should().BeNull();
    }

    [Fact]
    public void TryParseChallenge_RequiresRealmAndNonce()
    {
        // Nonce missing → must reject (cannot proceed).
        var ok = DigestAuthenticator.TryParseChallenge("Digest realm=\"r\"", out var ch);
        ok.Should().BeFalse();
        ch.Should().BeNull();
    }

    [Fact]
    public void Md5Sess_AppendsSessHashWithCnonce()
    {
        // For *-sess, HA1 changes shape; just verify the header is emitted with sess algorithm
        // and the response field is populated (deeper validation lives at integration level).
        var challenge = new DigestChallenge(
            Realm: "r", Nonce: "n", Algorithm: "MD5-sess", Qop: "auth",
            Opaque: null, Userhash: false);

        var result = DigestAuthenticator.BuildAuthorizationHeader(
            challenge, "GET", "/x", "u", "p", nonceCount: 1,
            cnonceProvider: new FixedCnonce("cnonce"));

        result.Value.Should().Contain("algorithm=MD5-sess");
        result.Value.Should().Contain("response=\"");
    }

    [Fact]
    public void NoQop_LegacyRFC2069_OmitsNcAndCnonce()
    {
        var challenge = new DigestChallenge(
            Realm: "r", Nonce: "n", Algorithm: "MD5", Qop: null,
            Opaque: null, Userhash: false);

        var result = DigestAuthenticator.BuildAuthorizationHeader(
            challenge, "GET", "/x", "u", "p", nonceCount: 1);

        result.Value.Should().NotContain("qop=");
        result.Value.Should().NotContain("nc=");
        result.Value.Should().NotContain("cnonce=");
    }

    [Fact]
    public void NonceCount_FormattedAs8DigitHex()
    {
        var challenge = new DigestChallenge(
            Realm: "r", Nonce: "n", Algorithm: "MD5", Qop: "auth",
            Opaque: null, Userhash: false);

        var result = DigestAuthenticator.BuildAuthorizationHeader(
            challenge, "GET", "/x", "u", "p", nonceCount: 0x12,
            cnonceProvider: new FixedCnonce("c"));

        result.Value.Should().Contain("nc=00000012");
    }

    [Fact]
    public void MissingChallengeFields_Throw()
    {
        var noRealm = new DigestChallenge(Realm: "", Nonce: "n",
            Algorithm: "MD5", Qop: "auth", Opaque: null, Userhash: false);
        Assert.Throws<System.ArgumentException>(() =>
            DigestAuthenticator.BuildAuthorizationHeader(noRealm, "GET", "/", "u", "p"));

        var noNonce = new DigestChallenge(Realm: "r", Nonce: "",
            Algorithm: "MD5", Qop: "auth", Opaque: null, Userhash: false);
        Assert.Throws<System.ArgumentException>(() =>
            DigestAuthenticator.BuildAuthorizationHeader(noNonce, "GET", "/", "u", "p"));
    }

    [Fact]
    public void Sha512_256_HashesAreTruncatedTo32Bytes()
    {
        // SHA-512-256 ≠ SHA-512: it's the first 32 bytes of SHA-512 per RFC 7616 §6.
        // Just verify a header is emitted; full vector test would require a published fixture.
        var challenge = new DigestChallenge(
            Realm: "r", Nonce: "n", Algorithm: "SHA-512-256", Qop: "auth",
            Opaque: null, Userhash: false);

        var result = DigestAuthenticator.BuildAuthorizationHeader(
            challenge, "GET", "/x", "u", "p", nonceCount: 1,
            cnonceProvider: new FixedCnonce("c"));

        result.Value.Should().Contain("algorithm=SHA-512-256");
        result.Value.Should().Contain("response=\"");
        // Hex-encoded 32 bytes = 64 chars.
        var responseHex = ExtractField(result.Value, "response");
        responseHex.Length.Should().Be(64);
    }

    private static string ExtractField(string header, string field)
    {
        var key = $"{field}=\"";
        var i = header.IndexOf(key, System.StringComparison.Ordinal);
        Assert.True(i >= 0, $"field '{field}' not found in {header}");
        i += key.Length;
        var j = header.IndexOf('"', i);
        return header.Substring(i, j - i);
    }
}
