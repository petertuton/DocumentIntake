namespace DocumentIntake.Functions.Options;

public sealed class DataverseOptions
{
    public const string SectionName = "Dataverse";

    public string EnvironmentUrl { get; set; } = string.Empty;
    public string EntitySetName { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "v9.2";

    /// <summary>
    /// When false the Dataverse step is skipped (no-op) so the rest of the
    /// pipeline can run before an auth strategy is finalised.
    /// </summary>
    public bool Enabled { get; set; }
}
