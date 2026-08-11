using System.Text.Json.Serialization;

namespace DocumentIntake.Functions.Models;

/// <summary>Location of a field on the source document.</summary>
public sealed record BoundingBox(
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("polygon")] IReadOnlyList<double> Polygon);

/// <summary>A single extracted field mapped onto a form column.</summary>
public sealed record FieldValue(
    [property: JsonPropertyName("column")] string Column,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("boundingBox")] BoundingBox? BoundingBox);

/// <summary>The payload posted to Dataverse for one processed document.</summary>
public sealed record MappedForm
{
    [JsonPropertyName("formType")]
    public string FormType { get; init; } = string.Empty;

    [JsonPropertyName("sourceBlobName")]
    public string SourceBlobName { get; init; } = string.Empty;

    [JsonPropertyName("completedBlobUrl")]
    public string? CompletedBlobUrl { get; init; }

    [JsonPropertyName("processedUtc")]
    public DateTimeOffset ProcessedUtc { get; init; }

    [JsonPropertyName("classificationConfidence")]
    public double ClassificationConfidence { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<FieldValue> Fields { get; init; } = [];
}
