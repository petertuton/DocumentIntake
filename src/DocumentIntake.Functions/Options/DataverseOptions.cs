namespace DocumentIntake.Functions.Options;

/// <summary>
/// Selects the credential used to authenticate calls to the Dataverse Web API.
/// </summary>
public enum DataverseAuthMode
{
    /// <summary>
    /// Uses the Function App's managed identity or the local development credential.
    /// </summary>
    ManagedIdentity,

    /// <summary>
    /// Uses an application registration in the tenant that owns the Dataverse environment.
    /// </summary>
    ClientSecret,
}

/// <summary>
/// Configures the Dataverse Web API integration.
/// </summary>
public sealed class DataverseOptions
{
    public const string SectionName = "Dataverse";

    public string EnvironmentUrl { get; set; } = string.Empty;
    public string EntitySetName { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "v9.2";
    public DataverseAuthMode AuthMode { get; set; } = DataverseAuthMode.ManagedIdentity;
    public string ClientSecretTenantId { get; set; } = string.Empty;
    public string ClientSecretClientId { get; set; } = string.Empty;
    public string ClientSecretValue { get; set; } = string.Empty;

    /// <summary>
    /// When false the Dataverse step is skipped (no-op) so the rest of the
    /// pipeline can run before an auth strategy is finalised.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Per-document-classification overrides for the environment, entity set, and (when
    /// <see cref="AuthMode"/> is <see cref="DataverseAuthMode.ClientSecret"/>) the app
    /// registration used to reach that environment. A list (not a dictionary) because Azure
    /// App Service settings become environment variables, and classification names such as
    /// "hipp-application" contain characters that aren't valid there as dictionary keys.
    /// Add an entry here for every new classification that needs its own target environment,
    /// entity set, or credentials.
    /// </summary>
    public List<DataverseFormMapping> FormMappings { get; set; } = [];

    /// <summary>Resolves the environment, entity set, and credentials to use for a document's classification.</summary>
    public DataverseTarget Resolve(string? formType)
    {
        var mapping = formType is null
            ? null
            : FormMappings.Find(candidate => string.Equals(candidate.Classification, formType, StringComparison.OrdinalIgnoreCase));

        if (mapping is not null)
        {
            return new DataverseTarget(
                string.IsNullOrWhiteSpace(mapping.EnvironmentUrl) ? EnvironmentUrl : mapping.EnvironmentUrl,
                string.IsNullOrWhiteSpace(mapping.EntitySetName) ? EntitySetName : mapping.EntitySetName,
                string.IsNullOrWhiteSpace(mapping.ClientSecretTenantId) ? ClientSecretTenantId : mapping.ClientSecretTenantId,
                string.IsNullOrWhiteSpace(mapping.ClientSecretClientId) ? ClientSecretClientId : mapping.ClientSecretClientId,
                string.IsNullOrWhiteSpace(mapping.ClientSecretValue) ? ClientSecretValue : mapping.ClientSecretValue);
        }

        return new DataverseTarget(EnvironmentUrl, EntitySetName, ClientSecretTenantId, ClientSecretClientId, ClientSecretValue);
    }
}

/// <summary>Per-classification override of the Dataverse environment, entity set, and app registration.</summary>
public sealed class DataverseFormMapping
{
    /// <summary>The Content Understanding category this mapping applies to (e.g. "hipp-application").</summary>
    public string Classification { get; set; } = string.Empty;
    public string EnvironmentUrl { get; set; } = string.Empty;
    public string EntitySetName { get; set; } = string.Empty;
    public string ClientSecretTenantId { get; set; } = string.Empty;
    public string ClientSecretClientId { get; set; } = string.Empty;
    public string ClientSecretValue { get; set; } = string.Empty;
}

/// <summary>The resolved Dataverse target for one document classification.</summary>
public sealed record DataverseTarget(
    string EnvironmentUrl,
    string EntitySetName,
    string ClientSecretTenantId,
    string ClientSecretClientId,
    string ClientSecretValue);
