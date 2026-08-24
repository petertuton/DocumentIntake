using System.Text.Json;
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

/// <summary>Raw analyze result returned by Content Understanding; Payload holds the extracted "result" node once succeeded.</summary>
public sealed record AnalysisResult(
    [property: JsonPropertyName("status")] AnalysisStatus Status,
    [property: JsonPropertyName("payload"), JsonConverter(typeof(RawJsonStringConverter))] string? Payload,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>
/// Serializes a string containing JSON as embedded raw JSON instead of an escaped JSON string,
/// so tools like the Durable Functions Monitor render <see cref="AnalysisResult.Payload"/> as
/// readable nested JSON rather than a "{\"...\":...}" blob. Plain, non-JSON strings pass through
/// unchanged, and reading always yields a plain string back for existing string-based consumers.
/// </summary>
internal sealed class RawJsonStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.GetRawText();
        }

        return reader.GetString();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            writer.WriteRawValue(value);
            return;
        }

        writer.WriteStringValue(value);
    }
}
