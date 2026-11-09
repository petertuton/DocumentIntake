namespace DocumentIntake.Functions.Options;

public sealed class ContentUnderstandingOptions
{
    public const string SectionName = "ContentUnderstanding";
    public const string DefaultFormCategory = "hipp-application-virginia";

    public string Endpoint { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2025-11-01";
    public string ClassifierId { get; set; } = "document_intake_classifier";
    public string AnalyzerId { get; set; } = "document_intake_form_analyzer";

    /// <summary>Category accepted when no explicit category list is configured.</summary>
    public string KnownFormCategory { get; set; } = DefaultFormCategory;

    /// <summary>Accepted form categories; an empty list falls back to <see cref="KnownFormCategory"/>.</summary>
    public List<string> KnownFormCategories { get; set; } = [];

    /// <summary>Classifications below this confidence are treated as unknown.</summary>
    public double MinimumClassificationConfidence { get; set; } = 0.6;

    /// <summary>Files larger than this are routed straight to the failed container.</summary>
    public long MaxDocumentSizeBytes { get; set; } = 200L * 1024 * 1024;
}
