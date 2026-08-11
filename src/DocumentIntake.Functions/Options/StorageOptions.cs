namespace DocumentIntake.Functions.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string BlobServiceUri { get; set; } = string.Empty;
}
