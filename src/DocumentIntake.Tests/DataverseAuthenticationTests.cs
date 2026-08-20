using Azure.Core;
using Azure.Identity;
using DocumentIntake.Functions.Options;
using DocumentIntake.Functions.Services;

namespace DocumentIntake.Tests;

public sealed class DataverseAuthenticationTests
{
    private static readonly TokenCredential ManagedIdentityCredential = new TestTokenCredential();

    [Fact]
    public void GivenClientSecretMode_WhenCreatingCredential_ReturnsClientSecretCredential()
    {
        var factory = new DataverseTokenCredentialFactory(ManagedIdentityCredential);
        var options = CreateEnabledOptions(DataverseAuthMode.ClientSecret);

        var credential = factory.Create(options, options.Resolve(null));

        Assert.IsType<ClientSecretCredential>(credential);
    }

    [Fact]
    public void GivenDisabledDataverse_WhenCreatingCredential_ReturnsManagedIdentityCredential()
    {
        var factory = new DataverseTokenCredentialFactory(ManagedIdentityCredential);
        var options = new DataverseOptions { AuthMode = DataverseAuthMode.ClientSecret };

        var credential = factory.Create(options, options.Resolve(null));

        Assert.Same(ManagedIdentityCredential, credential);
    }

    [Fact]
    public void GivenManagedIdentityMode_WhenCreatingCredential_ReturnsManagedIdentityCredential()
    {
        var factory = new DataverseTokenCredentialFactory(ManagedIdentityCredential);
        var options = CreateEnabledOptions(DataverseAuthMode.ManagedIdentity);

        var credential = factory.Create(options, options.Resolve(null));

        Assert.Same(ManagedIdentityCredential, credential);
    }

    [Theory]
    [InlineData(nameof(DataverseOptions.ClientSecretTenantId))]
    [InlineData(nameof(DataverseOptions.ClientSecretClientId))]
    [InlineData(nameof(DataverseOptions.ClientSecretValue))]
    public void GivenClientSecretModeWithMissingSetting_WhenValidating_ReturnsFailure(string missingProperty)
    {
        var options = CreateEnabledOptions(DataverseAuthMode.ClientSecret);
        typeof(DataverseOptions).GetProperty(missingProperty)!.SetValue(options, string.Empty);

        var result = new DataverseOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void GivenDisabledDataverse_WhenValidating_ReturnsSuccess()
    {
        var result = new DataverseOptionsValidator().Validate(null, new DataverseOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void GivenValidClientSecretMode_WhenValidating_ReturnsSuccess()
    {
        var result = new DataverseOptionsValidator().Validate(null, CreateEnabledOptions(DataverseAuthMode.ClientSecret));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void GivenValidManagedIdentityMode_WhenValidating_ReturnsSuccess()
    {
        var result = new DataverseOptionsValidator().Validate(null, CreateEnabledOptions(DataverseAuthMode.ManagedIdentity));

        Assert.True(result.Succeeded);
    }

    private static DataverseOptions CreateEnabledOptions(DataverseAuthMode authMode) => new()
    {
        AuthMode = authMode,
        ClientSecretClientId = "00000000-0000-0000-0000-000000000001",
        ClientSecretTenantId = "00000000-0000-0000-0000-000000000002",
        ClientSecretValue = "not-a-real-secret",
        Enabled = true,
        EntitySetName = "new_documentintakes",
        EnvironmentUrl = "https://contoso.crm.dynamics.com",
    };

    private sealed class TestTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}