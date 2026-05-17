using Amazon.Runtime;
using Azure.Identity;
using Vegha.Integrations.Secrets.Aws;
using Vegha.Integrations.Secrets.Azure;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Secrets;

/// <summary>Covers credential selection from a provider's config dictionary — the auth
/// fields (Azure service principal, AWS IAM keys) entered on the Secret Manager settings
/// page must produce the right SDK credential type.</summary>
public class ProviderCredentialTests
{
    // ----- Azure Key Vault ---------------------------------------------------------------

    [Fact]
    public void Azure_AllServicePrincipalFields_UsesClientSecretCredential()
    {
        var cred = AzureKeyVaultProvider.BuildCredential(new Dictionary<string, string>
        {
            ["vaultUri"] = "https://v.vault.azure.net/",
            ["tenantId"] = "11111111-1111-1111-1111-111111111111",
            ["clientId"] = "22222222-2222-2222-2222-222222222222",
            ["clientSecret"] = "the-secret",
        });

        cred.Should().BeOfType<ClientSecretCredential>();
    }

    [Fact]
    public void Azure_NoServicePrincipalFields_FallsBackToDefaultCredential()
    {
        var cred = AzureKeyVaultProvider.BuildCredential(new Dictionary<string, string>
        {
            ["vaultUri"] = "https://v.vault.azure.net/",
        });

        cred.Should().BeOfType<DefaultAzureCredential>();
    }

    [Fact]
    public void Azure_PartialServicePrincipalFields_FallsBackToDefaultCredential()
    {
        // Missing clientSecret — incomplete group does not produce a service principal.
        var cred = AzureKeyVaultProvider.BuildCredential(new Dictionary<string, string>
        {
            ["tenantId"] = "11111111-1111-1111-1111-111111111111",
            ["clientId"] = "22222222-2222-2222-2222-222222222222",
        });

        cred.Should().BeOfType<DefaultAzureCredential>();
    }

    [Fact]
    public void Azure_FromConfig_WithoutVaultUri_Throws()
    {
        var act = () => AzureKeyVaultProvider.FromConfig(new Dictionary<string, string>());
        act.Should().Throw<ArgumentException>();
    }

    // ----- AWS Secrets Manager -----------------------------------------------------------

    [Fact]
    public void Aws_AccessKeyPair_UsesBasicCredentials()
    {
        var creds = AwsSecretsProvider.BuildCredentials(new Dictionary<string, string>
        {
            ["region"] = "us-east-1",
            ["accessKeyId"] = "AKIAEXAMPLE",
            ["secretAccessKey"] = "secret",
        });

        creds.Should().BeOfType<BasicAWSCredentials>();
    }

    [Fact]
    public void Aws_AccessKeyPairWithSessionToken_UsesSessionCredentials()
    {
        var creds = AwsSecretsProvider.BuildCredentials(new Dictionary<string, string>
        {
            ["region"] = "us-east-1",
            ["accessKeyId"] = "AKIAEXAMPLE",
            ["secretAccessKey"] = "secret",
            ["sessionToken"] = "token",
        });

        creds.Should().BeOfType<SessionAWSCredentials>();
    }

    [Fact]
    public void Aws_NoKeys_ReturnsNull_ForDefaultCredentialChain()
    {
        var creds = AwsSecretsProvider.BuildCredentials(new Dictionary<string, string>
        {
            ["region"] = "us-east-1",
        });

        creds.Should().BeNull();
    }

    [Fact]
    public void Aws_FromConfig_WithoutRegion_Throws()
    {
        var act = () => AwsSecretsProvider.FromConfig(new Dictionary<string, string>());
        act.Should().Throw<ArgumentException>();
    }
}
