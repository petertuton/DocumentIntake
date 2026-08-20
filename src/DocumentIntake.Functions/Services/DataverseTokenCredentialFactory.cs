using Azure.Core;
using Azure.Identity;
using DocumentIntake.Functions.Options;

namespace DocumentIntake.Functions.Services;

/// <summary>
/// Creates the token credential used only for Dataverse Web API requests.
/// </summary>
public sealed class DataverseTokenCredentialFactory(TokenCredential managedIdentityCredential)
{
    /// <summary>
    /// Gets the credential selected by the Dataverse configuration, using the
    /// classification-resolved <paramref name="target"/> for the app registration
    /// when <see cref="DataverseAuthMode.ClientSecret"/> is in effect.
    /// </summary>
    /// <param name="options">The Dataverse configuration.</param>
    /// <param name="target">The resolved environment, entity set, and credentials for the document's classification.</param>
    /// <returns>The configured token credential.</returns>
    public TokenCredential Create(DataverseOptions options, DataverseTarget target)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(target);

        if (!options.Enabled || options.AuthMode == DataverseAuthMode.ManagedIdentity)
        {
            return managedIdentityCredential;
        }

        if (options.AuthMode == DataverseAuthMode.ClientSecret)
        {
            return new ClientSecretCredential(
                target.ClientSecretTenantId,
                target.ClientSecretClientId,
                target.ClientSecretValue);
        }

        throw new InvalidOperationException($"Unsupported Dataverse authentication mode: {options.AuthMode}.");
    }
}