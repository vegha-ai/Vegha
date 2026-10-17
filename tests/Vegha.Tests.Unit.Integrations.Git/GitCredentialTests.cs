using FluentAssertions;
using Vegha.Integrations.Git;
using Vegha.Integrations.Git.Credentials;
using Vegha.Integrations.Git.Providers;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Git;

public sealed class GitCredentialTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vegha-cred-" + Guid.NewGuid().ToString("N"));

    public GitCredentialTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ----- URL parsing / normalization --------------------------------------

    [Theory]
    [InlineData("https://github.com/acme/api.git", "https", "github.com", "acme/api.git", "api")]
    [InlineData("git@github.com:acme/api.git", "ssh", "github.com", "acme/api.git", "api")]
    [InlineData("ssh://git@ssh.github.com:443/acme/api.git", "ssh", "ssh.github.com", "acme/api.git", "api")]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Proj/repo", "ssh", "ssh.dev.azure.com", "v3/contoso/Proj/repo", "repo")]
    [InlineData("https://contoso@dev.azure.com/contoso/Proj/_git/repo", "https", "dev.azure.com", "contoso/Proj/_git/repo", "repo")]
    public void GitRemoteUrl_Parses(string url, string scheme, string host, string path, string repoName)
    {
        var r = GitRemoteUrl.TryParse(url)!;
        r.Scheme.Should().Be(scheme);
        r.Host.Should().Be(host);
        r.Path.Should().Be(path);
        r.RepoName.Should().Be(repoName);
    }

    [Fact]
    public void GitRemoteUrl_WindowsPath_IsFile_NotScp()
    {
        var r = GitRemoteUrl.TryParse(@"C:\repos\server.git")!;
        r.Scheme.Should().Be("file");
        r.RepoName.Should().Be("server");
    }

    [Theory]
    [InlineData("https://contoso.visualstudio.com/Proj/_git/repo", "https://dev.azure.com/contoso/Proj/_git/repo")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection/Proj/_git/repo", "https://dev.azure.com/contoso/Proj/_git/repo")]
    [InlineData("https://contoso@dev.azure.com/contoso/Proj/_git/repo", "https://dev.azure.com/contoso/Proj/_git/repo")]
    [InlineData("https://github.com/acme/api", "https://github.com/acme/api.git")]
    [InlineData("git@github.com:acme/api.git", "git@github.com:acme/api.git")]
    public void Provider_Normalize(string input, string expected) =>
        GitProviderRegistry.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("https://github.com/exeloncorp/EU-AccountQuery/tree/dev/Postman", "https://github.com/exeloncorp/EU-AccountQuery.git", "dev", "Postman")]
    [InlineData("https://github.com/acme/api/tree/main/postman/collections", "https://github.com/acme/api.git", "main", "postman/collections")]
    [InlineData("https://github.com/acme/api/blob/main/postman/x.postman_collection.json", "https://github.com/acme/api.git", "main", "postman")]
    [InlineData("https://github.com/acme/api/tree/main", "https://github.com/acme/api.git", "main", null)]
    [InlineData("https://dev.azure.com/contoso/Proj/_git/repo?path=/Postman&version=GBdev", "https://dev.azure.com/contoso/Proj/_git/repo", "dev", "Postman")]
    [InlineData("https://dev.azure.com/contoso/Proj/_git/repo?path=/Postman/a.json&version=GBmain", "https://dev.azure.com/contoso/Proj/_git/repo", "main", "Postman")]
    public void ParseWebUrl_BrowserLinks(string url, string clone, string? branch, string? folder)
    {
        var web = GitProviderRegistry.ParseWebUrl(url)!;
        web.CloneUrl.Should().Be(clone);
        web.Branch.Should().Be(branch);
        web.Folder.Should().Be(folder);
    }

    [Theory]
    [InlineData("https://github.com/acme/api.git")]
    [InlineData("https://github.com/acme/api")]
    [InlineData("git@github.com:acme/api.git")]
    [InlineData("https://dev.azure.com/contoso/Proj/_git/repo")]
    public void ParseWebUrl_IgnoresCloneUrls(string url) => GitProviderRegistry.ParseWebUrl(url).Should().BeNull();

    [Theory]
    [InlineData("https://dev.azure.com/contoso/Proj/_git/repo", "contoso")]
    [InlineData("git@ssh.dev.azure.com:v3/fabrikam/Proj/repo", "fabrikam")]
    [InlineData("https://litware.visualstudio.com/Proj/_git/repo", "litware")]
    public void AzureDevOps_Organization(string url, string org) =>
        AzureDevOpsProvider.Organization(GitRemoteUrl.TryParse(url)!).Should().Be(org);

    [Theory]
    [InlineData("https://github.com", "https://api.github.com")]
    [InlineData("https://acme.ghe.com", "https://api.acme.ghe.com")]
    [InlineData("https://github.acme.com", "https://github.acme.com/api/v3")]
    public void GitHub_ApiBase(string baseUrl, string api) => GitHubProvider.ApiBase(baseUrl).Should().Be(api);

    // ----- profile resolution -----------------------------------------------

    [Fact]
    public void ProfileResolution_RepoOverride_ThenLongestPrefix_ThenNone()
    {
        var store = new GitCredentialProfileStore(Path.Combine(_dir, "p.json"));
        var host = new GitCredentialProfile { Id = "host", HostPattern = "dev.azure.com" };
        var org = new GitCredentialProfile { Id = "org", HostPattern = "dev.azure.com/contoso" };
        var personal = new GitCredentialProfile { Id = "personal", HostPattern = "github.com" };
        store.Upsert(host);
        store.Upsert(org);
        store.Upsert(personal);

        store.Resolve("https://dev.azure.com/contoso/P/_git/r", null)!.Id.Should().Be("org");
        store.Resolve("https://dev.azure.com/fabrikam/P/_git/r", null)!.Id.Should().Be("host");
        store.Resolve("https://dev.azure.com/contosoX/P/_git/r", null)!.Id.Should().Be("host", "prefixes match whole path segments");
        store.Resolve("https://gitlab.com/a/b.git", null).Should().BeNull();

        var repo = Path.Combine(_dir, "repo");
        store.SetRepoOverride(repo, "personal");
        store.Resolve("https://dev.azure.com/contoso/P/_git/r", repo)!.Id.Should().Be("personal");

        store.Remove("personal");
        store.Resolve("https://dev.azure.com/contoso/P/_git/r", repo)!.Id.Should().Be("org", "removing a profile drops its overrides");
    }

    // ----- environment injection --------------------------------------------

    [Fact]
    public void GitEnvironment_WritesConfigCountAndBaseline()
    {
        var env = new GitEnvironment().AddConfig("http.proxy", "http://p:8080").AddConfig("credential.helper", "");
        var target = new Dictionary<string, string?>();

        env.ApplyTo(target);

        target["GIT_TERMINAL_PROMPT"].Should().Be("0");
        target["LC_ALL"].Should().Be("C");
        target["GIT_CONFIG_COUNT"].Should().Be("2");
        target["GIT_CONFIG_KEY_0"].Should().Be("http.proxy");
        target["GIT_CONFIG_VALUE_1"].Should().Be("");
    }

    [Fact]
    public void GitEnvironment_AppendsAfterInheritedConfigCount()
    {
        var target = new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "a.b", ["GIT_CONFIG_VALUE_0"] = "c" };
        new GitEnvironment().AddConfig("x.y", "z").ApplyTo(target);
        target["GIT_CONFIG_COUNT"].Should().Be("2");
        target["GIT_CONFIG_KEY_1"].Should().Be("x.y");
        target["GIT_CONFIG_KEY_0"].Should().Be("a.b");
    }

    [Fact]
    public async Task Broker_IsolatedPat_InjectsHostScopedHeader_AndResetsHelper()
    {
        var store = new GitCredentialProfileStore(Path.Combine(_dir, "p.json"));
        var vault = new GitSecretVault(_dir);
        var broker = new GitCredentialBroker(store, vault, new ExternalCli()) { Options = new GitGlobalOptions { UseOsCertificateStore = false, UseWindowsBroker = false, PreferWindowsOpenSsh = false } };
        var profile = new GitCredentialProfile { Id = "p1", HostPattern = "github.com", Method = GitAuthMethod.Pat, StoreMode = GitSecretStoreMode.VeghaIsolated };
        store.Upsert(profile);
        vault.Set("p1", "ghp_secret");

        var env = await broker.BuildAsync(new GitEnvironmentRequest("push", "https://github.com/acme/api.git", _dir), CancellationToken.None);

        var cfg = env.Config.ToDictionary(kv => kv.Key, kv => kv.Value);
        cfg.Should().ContainKey("credential.https://github.com.helper").WhoseValue.Should().BeEmpty();
        var header = cfg["http.https://github.com/.extraHeader"];
        header.Should().StartWith("Authorization: Basic ");
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header["Authorization: Basic ".Length..]));
        decoded.Should().Be("x-access-token:ghp_secret");
    }

    [Fact]
    public async Task Broker_AzureDevOps_KeysCredentialsByPath_AndProxyIsApplied()
    {
        var store = new GitCredentialProfileStore(Path.Combine(_dir, "p.json"));
        var broker = new GitCredentialBroker(store, new GitSecretVault(_dir), new ExternalCli())
        {
            ProxyResolver = _ => new GitProxy("http://proxy.acme:3128", "localhost"),
        };

        var env = await broker.BuildAsync(new GitEnvironmentRequest("fetch", "https://dev.azure.com/contoso/P/_git/r", _dir), CancellationToken.None);

        env.Config.Should().Contain(kv => kv.Key == "credential.https://dev.azure.com.useHttpPath" && kv.Value == "true");
        env.Config.Should().Contain(kv => kv.Key == "http.proxy" && kv.Value == "http://proxy.acme:3128");
        env.Variables["NO_PROXY"].Should().Be("localhost");
    }

    [Fact]
    public async Task Broker_SshProfile_SetsSshCommandWithKey()
    {
        var store = new GitCredentialProfileStore(Path.Combine(_dir, "p.json"));
        store.Upsert(new GitCredentialProfile { HostPattern = "github.com", Method = GitAuthMethod.Ssh, SshKeyPath = @"C:\Users\me\.ssh\id_work" });
        var broker = new GitCredentialBroker(store, new GitSecretVault(_dir), new ExternalCli()) { Options = new GitGlobalOptions { PreferWindowsOpenSsh = false } };

        var env = await broker.BuildAsync(new GitEnvironmentRequest("fetch", "git@github.com:acme/api.git", _dir), CancellationToken.None);

        env.Config.Last(kv => kv.Key == "core.sshCommand").Value
            .Should().Be("'ssh' -i 'C:/Users/me/.ssh/id_work' -o IdentitiesOnly=yes");
    }

    [Fact]
    public async Task Broker_Defaults_NeverOverrideUserConfig()
    {
        var store = new GitCredentialProfileStore(Path.Combine(_dir, "p.json"));
        var broker = new GitCredentialBroker(store, new GitSecretVault(_dir), new ExternalCli())
        {
            Options = new GitGlobalOptions(),
            // User has configured all of these themselves.
            UserHasConfig = (_, _) => true,
        };

        var env = await broker.BuildAsync(new GitEnvironmentRequest("fetch", "https://dev.azure.com/contoso/P/_git/r", _dir), CancellationToken.None);

        env.Config.Select(kv => kv.Key).Should().NotContain(new[]
        {
            "http.sslBackend", "credential.msauthUseBroker", "core.sshCommand", "credential.azreposCredentialType",
        });
    }

    [Fact]
    public void Vault_RoundTrips_AndDoesNotStorePlaintext()
    {
        var vault = new GitSecretVault(_dir);
        vault.Set("k", "super-secret-token");

        new GitSecretVault(_dir).Get("k").Should().Be("super-secret-token");
        File.ReadAllText(Path.Combine(_dir, "git-secrets.json")).Should().NotContain("super-secret-token");

        vault.Delete("k");
        vault.Get("k").Should().BeNull();
    }
}
