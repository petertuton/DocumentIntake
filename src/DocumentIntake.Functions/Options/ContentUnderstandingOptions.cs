namespace DocumentIntake.Functions.Options;

public sealed class ContentUnderstandingOptions
{
    public const string SectionName = "ContentUnderstanding";

    public string Endpoint { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2025-11-01";
    public string ClassifierId { get; set; } = "document-intake-classifier";
    public string AnalyzerId { get; set; } = "document-intake-form-analyzer";

    /// <summary>Category emitted by the classifier for the single known form.</summary>
    public string KnownFormCategory { get; set; } = "known-form";

    /// <summary>Classifications below this confidence are treated as unknown.</summary>
    public double MinimumClassificationConfidence { get; set; } = 0.6;

    /// <summary>Files larger than this are routed straight to the failed container.</summary>
    public long MaxDocumentSizeBytes { get; set; } = 200L * 1024 * 1024;
}
