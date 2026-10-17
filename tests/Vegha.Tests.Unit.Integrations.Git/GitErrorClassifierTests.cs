using FluentAssertions;
using Vegha.Integrations.Git;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Git;

/// <summary>Stderr fixtures captured from git / GCM / ssh against GitHub, Azure DevOps and
/// generic servers, mapped to actionable error kinds.</summary>
public class GitErrorClassifierTests
{
    [Theory]
    // The screenshot that started this work.
    [InlineData("fatal: 'origin' does not appear to be a git repository\nfatal: Could not read from remote repository.", GitErrorKind.RemoteNotFound)]
    [InlineData("fatal: No configured push destination.", GitErrorKind.NoRemoteConfigured)]
    [InlineData("remote: The 'acme' organization has enabled or enforced SAML SSO. To access\nremote: this repository, visit https://github.com/orgs/acme/sso?authorization_request=ABC123 and try your request again.", GitErrorKind.SamlSsoAuthorizationRequired)]
    [InlineData("fatal: Authentication failed for 'https://dev.azure.com/contoso/proj/_git/repo/'", GitErrorKind.AuthFailed)]
    [InlineData("remote: TF400813: The user 'x' is not authorized to access this resource.\nfatal: Authentication failed", GitErrorKind.AuthFailed)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", GitErrorKind.AuthFailed)]
    [InlineData("remote: TF401019: The Git repository with name or identifier repo does not exist or you do not have permissions", GitErrorKind.RepoNotFoundOrNoAccess)]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/acme/nope.git/' not found", GitErrorKind.RepoNotFoundOrNoAccess)]
    [InlineData("Host key verification failed.\nfatal: Could not read from remote repository.", GitErrorKind.HostKeyUnknown)]
    [InlineData("@@@@@@@@@@@@\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @", GitErrorKind.HostKeyChanged)]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", GitErrorKind.PermissionDeniedPublicKey)]
    [InlineData("fatal: unable to access 'https://git.acme.local/r.git/': SSL certificate problem: unable to get local issuer certificate", GitErrorKind.SslCertProblem)]
    [InlineData("fatal: unable to access 'https://github.com/a/b.git/': Received HTTP code 407 from proxy after CONNECT\n407 Proxy Authentication Required", GitErrorKind.ProxyAuthRequired)]
    [InlineData("fatal: unable to access 'https://github.com/a/b.git/': Could not resolve host: github.com", GitErrorKind.HostUnreachable)]
    [InlineData(" ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs\nhint: Updates were rejected because the remote contains work", GitErrorKind.NonFastForward)]
    [InlineData("fatal: Not possible to fast-forward, aborting.", GitErrorKind.NonFastForward)]
    [InlineData("fatal: refusing to merge unrelated histories", GitErrorKind.UnrelatedHistories)]
    [InlineData("CONFLICT (content): Merge conflict in a.bru\nerror: could not apply 1234abc... change", GitErrorKind.MergeConflict)]
    [InlineData("error: Your local changes to the following files would be overwritten by merge:", GitErrorKind.LocalChangesWouldBeOverwritten)]
    [InlineData("fatal: something nobody has seen before", GitErrorKind.Unknown)]
    public void Classify_MapsKnownFailures(string stderr, GitErrorKind expected) =>
        GitErrorClassifier.Classify(stderr, "push").Kind.Should().Be(expected);

    [Fact]
    public void RemoteNotFound_OffersAddRemoteAndPublish()
    {
        var info = GitErrorClassifier.Classify("fatal: 'origin' does not appear to be a git repository", "push");
        info.Detail.Should().Be("origin");
        info.Actions.Should().Contain(new[] { GitErrorAction.AddRemote, GitErrorAction.PublishRepository });
    }

    [Fact]
    public void SamlSso_ExtractsAuthorizationUrl()
    {
        var info = GitErrorClassifier.Classify(
            "remote: visit https://github.com/orgs/acme/sso?authorization_request=ABC123 and try again.", "fetch");
        info.Url.Should().Be("https://github.com/orgs/acme/sso?authorization_request=ABC123");
        info.Actions.Should().Contain(GitErrorAction.OpenSsoAuthorization);
    }

    [Fact]
    public void LocalPathRemote_IsNotMistakenForRemoteName()
    {
        // A file-path URL that doesn't exist is "couldn't read", not "no remote named …".
        var info = GitErrorClassifier.Classify("fatal: '/tmp/x/nope.git' does not appear to be a git repository", "clone");
        info.Kind.Should().Be(GitErrorKind.RepoNotFoundOrNoAccess);
    }
}
