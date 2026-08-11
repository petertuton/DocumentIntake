using System.Text.Json.Serialization;

namespace DocumentIntake.Functions.Models;

/// <summary>Outcome of the Content Understanding classifier for one document.</summary>
public sealed record ClassificationResult(
    [property: JsonPropertyName("isKnownForm")] bool IsKnownForm,
    [property: JsonPropertyName("formType")] string? FormType,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>Handle to a long-running Content Understanding analyze operation.</summary>
public sealed record AnalysisOperation(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("operationLocation")] string OperationLocation);

/// <summary>Status of a polled analyze operation.</summary>
public enum AnalysisStatus
{
    NotStarted,
    Running,
    Succeeded,
    Failed
}

/// <summary>Raw analyze result returned by Content Understanding.</summary>
public sealed record AnalysisResult(
    [property: JsonPropertyName("status")] AnalysisStatus Status,
    [property: JsonPropertyName("payload")] string? Payload,
    [property: JsonPropertyName("error")] string? Error = null);
