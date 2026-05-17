using System.Net;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Persistence;

public class ProxyResolverTests
{
    private static AppSettings WithProxy(string mode = "off", string protocol = "http",
        string host = "", int port = 0, bool auth = false, string user = "",
        string pw = "", string bypass = "")
    {
        var s = AppSettings.Default with
        {
            ProxyMode = mode,
            ProxyProtocol = protocol,
            ProxyHost = host,
            ProxyPort = port,
            ProxyAuthEnabled = auth,
            ProxyUsername = user,
            ProxyPasswordEncrypted = ProxyCredentialProtector.Protect(pw),
            ProxyBypass = bypass,
        };
        return s;
    }

    [Fact]
    public void Mode_Off_ReturnsNull()
    {
        ProxyResolver.Build(WithProxy("off")).Should().BeNull();
    }

    [Fact]
    public void Mode_System_ReturnsSystemProxy()
    {
        // GetSystemWebProxy may return varying types depending on platform — we only
        // assert it returns something rather than null, which proves the branch is hit.
        var p = ProxyResolver.Build(WithProxy("system"));
        p.Should().NotBeNull();
    }

    [Fact]
    public void Mode_On_HttpWithHostPort_BuildsWebProxy()
    {
        var p = ProxyResolver.Build(WithProxy("on", "http", "proxy.test", 3128));
        p.Should().NotBeNull();
        p!.GetProxy(new Uri("https://example.com"))!.Authority.Should().Be("proxy.test:3128");
    }

    [Fact]
    public void Mode_On_Https_BuildsWebProxy()
    {
        // Use a non-default port — WebProxy normalizes the URI Authority by stripping the
        // port when it matches the scheme's default (443 for https), which would make this
        // assertion fragile.
        var p = ProxyResolver.Build(WithProxy("on", "https", "proxy.test", 8443));
        p.Should().NotBeNull();
        var uri = p!.GetProxy(new Uri("https://example.com"))!;
        uri.Scheme.Should().Be("https");
        uri.Authority.Should().Be("proxy.test:8443");
    }

    [Theory]
    [InlineData("socks4")]
    [InlineData("socks5")]
    public void Mode_On_Socks_NotSupported_ReturnsNull(string protocol)
    {
        // SOCKS support requires an external handler we don't ship yet; the resolver
        // gracefully returns null instead of building a non-functional proxy.
        ProxyResolver.Build(WithProxy("on", protocol, "proxy.test", 1080)).Should().BeNull();
    }

    [Fact]
    public void Mode_On_MissingHost_ReturnsNull()
    {
        ProxyResolver.Build(WithProxy("on", "http", host: "", port: 3128)).Should().BeNull();
    }

    [Fact]
    public void Mode_On_MissingPort_ReturnsNull()
    {
        ProxyResolver.Build(WithProxy("on", "http", host: "proxy.test", port: 0)).Should().BeNull();
    }

    [Fact]
    public void Mode_On_WithAuth_AttachesCredentials()
    {
        var p = ProxyResolver.Build(WithProxy(
            "on", "http", "proxy.test", 3128,
            auth: true, user: "alice", pw: "secret"));
        p.Should().NotBeNull();
        var creds = p!.Credentials.Should().BeOfType<NetworkCredential>().Subject;
        creds.UserName.Should().Be("alice");
        creds.Password.Should().Be("secret");
    }

    [Fact]
    public void Mode_On_NoAuth_LeavesCredentialsUnset()
    {
        var p = ProxyResolver.Build(WithProxy("on", "http", "proxy.test", 3128, auth: false));
        p.Should().NotBeNull();
        p!.Credentials.Should().BeNull();
    }

    [Fact]
    public void Mode_On_BypassList_IsParsed()
    {
        var p = ProxyResolver.Build(WithProxy(
            "on", "http", "proxy.test", 3128,
            bypass: "localhost, *.corp.example.com\n10.0.0.0/8"));
        var wp = p as WebProxy;
        wp.Should().NotBeNull();
        // BypassList stores the regex form — verify the patterns include the original tokens
        // with `*` translated to `.*`.
        wp!.BypassList.Should().HaveCount(3);
        wp.BypassList[0].Should().Be("localhost");
        wp.BypassList[1].Should().Contain(".*").And.Contain("corp\\.example\\.com");
        wp.BypassList[2].Should().Contain("10\\.0\\.0\\.0/8");
    }

    [Fact]
    public void Mode_Pac_NoEvaluator_FallsBackToSystem()
    {
        // PAC isn't supported yet; resolver falls back to system proxy rather than null
        // so requests still flow through whatever the OS has configured.
        var p = ProxyResolver.Build(WithProxy("pac"));
        p.Should().NotBeNull();
    }
}
