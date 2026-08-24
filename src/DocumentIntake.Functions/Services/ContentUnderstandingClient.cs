using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentIntake.Functions.Services;

/// <summary>
/// Thin REST client over Azure AI Content Understanding. Authentication is handled by
/// a <see cref="DelegatingHandler"/> that attaches a managed identity bearer token.
/// </summary>
public sealed class ContentUnderstandingClient : IContentUnderstandingClient
{
    public const string HttpClientName = "content-understanding";

    private static readonly TimeSpan ClassifyPollInterval = TimeSpan.FromSeconds(2);
    private const int MaxClassifyPolls = 30;

    private readonly HttpClient _http;
    private readonly ContentUnderstandingOptions _options;
    private readonly ILogger<ContentUnderstandingClient> _logger;

    public ContentUnderstandingClient(
        HttpClient http,
        IOptions<ContentUnderstandingOptions> options,
        ILogger<ContentUnderstandingClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ClassificationResult> ClassifyAsync(Uri blobUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blobUri);

        var operation = await SubmitAsync(_options.ClassifierId, blobUri, cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < MaxClassifyPolls; attempt++)
        {
            var result = await GetAnalysisResultAsync(operation, cancellationToken).ConfigureAwait(false);

            switch (result.Status)
            {
                case AnalysisStatus.Succeeded:
                    return ParseClassification(result.Payload);
                case AnalysisStatus.Failed:
                    throw new InvalidOperationException($"Classification failed: {result.Error}");
                default:
                    await Task.Delay(ClassifyPollInterval, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        throw new TimeoutException("Classification did not complete within the allotted time.");
    }

    public Task<AnalysisOperation> SubmitAnalysisAsync(Uri blobUri, CancellationToken cancellationToken = default)
        => SubmitAsync(_options.AnalyzerId, blobUri, cancellationToken);

    public async Task<AnalysisResult> GetAnalysisResultAsync(
        AnalysisOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var response = await _http
            .GetAsync(operation.OperationLocation, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new AnalysisResult(AnalysisStatus.Failed, null, $"HTTP {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var status = document.RootElement.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : null;

        return status?.ToUpperInvariant() switch
        {
            "SUCCEEDED" => new AnalysisResult(AnalysisStatus.Succeeded, ExtractResultPayload(document.RootElement, body)),
            "FAILED" or "CANCELED" => new AnalysisResult(AnalysisStatus.Failed, body, ExtractError(document.RootElement)),
            "NOTSTARTED" => new AnalysisResult(AnalysisStatus.NotStarted, null),
            _ => new AnalysisResult(AnalysisStatus.Running, null),
        };
    }

    // Content Understanding embeds full OCR markdown/pages/tables per content entry, which can run into
    // multiple megabytes; the monitor and Dataverse only need the classification/extraction outcome.
    // The classifier reports its category inside "segments" (not directly on the content entry), so
    // that must be retained too or classification silently breaks.
    private static readonly string[] RetainedContentProperties = ["category", "confidence", "fields", "segments"];

    /// <summary>Trims the "result" node down to classification/field data so the monitor can render it.</summary>
    internal static string ExtractResultPayload(JsonElement root, string rawBody)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
        {
            return rawBody;
        }

        if (!result.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
        {
            return result.GetRawText();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("contents");

            foreach (var content in contents.EnumerateArray())
            {
                writer.WriteStartObject();

                foreach (var propertyName in RetainedContentProperties)
                {
                    if (!content.TryGetProperty(propertyName, out var value))
                    {
                        continue;
                    }

                    writer.WritePropertyName(propertyName);

                    // Each extracted field also echoes character "spans" into the source OCR text;
                    // nothing downstream uses them, so drop them to keep only name/type/value/confidence/source.
                    if (propertyName == "fields")
                    {
                        WriteWithoutSpans(value, writer);
                    }
                    else
                    {
                        value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteWithoutSpans(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("spans"))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteWithoutSpans(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteWithoutSpans(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private async Task<AnalysisOperation> SubmitAsync(string analyzerId, Uri blobUri, CancellationToken cancellationToken)
    {
        var requestUri = string.Create(
            CultureInfo.InvariantCulture,
            $"{_options.Endpoint.TrimEnd('/')}/contentunderstanding/analyzers/{analyzerId}:analyze?api-version={_options.ApiVersion}");

        var payload = new
        {
            inputs = new[] { new { url = blobUri.ToString() } },
        };

        using var response = await _http
            .PostAsJsonAsync(requestUri, payload, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Content Understanding analyze request failed with HTTP {(int)response.StatusCode}: {error}");
        }

        if (!response.Headers.TryGetValues("Operation-Location", out var locations))
        {
            throw new InvalidOperationException("Content Understanding response did not include an Operation-Location header.");
        }

        var operationLocation = locations.First();
        var operationId = ExtractOperationId(operationLocation);

        _logger.LogInformation(
            "Submitted {AnalyzerId} analysis; operation {OperationId}.",
            analyzerId,
            operationId);

        return new AnalysisOperation(operationId, operationLocation);
    }

    internal static string ExtractOperationId(string operationLocation)
    {
        var uri = new Uri(operationLocation, UriKind.Absolute);
        var segments = uri.Segments.Select(s => s.Trim('/')).Where(s => s.Length > 0).ToArray();
        return segments.Length > 0 ? segments[^1] : operationLocation;
    }

    private ClassificationResult ParseClassification(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new ClassificationResult(false, null, 0, "Empty classification payload.");
        }

        using var document = JsonDocument.Parse(payload);

        var category = FindFirstCategory(document.RootElement);
        var confidence = FindFirstConfidence(document.RootElement) ?? 1.0;

        if (category is null)
        {
            return new ClassificationResult(false, null, 0, "No category returned by classifier.");
        }

        var isKnown = string.Equals(category, _options.KnownFormCategory, StringComparison.OrdinalIgnoreCase)
            && confidence >= _options.MinimumClassificationConfidence;

        var reason = isKnown
            ? null
            : confidence < _options.MinimumClassificationConfidence
                ? $"Confidence {confidence:F2} below threshold {_options.MinimumClassificationConfidence:F2}."
                : $"Category '{category}' is not a known form.";

        return new ClassificationResult(isKnown, category, confidence, reason);
    }

    internal static string? FindFirstCategory(JsonElement element)
        => FindFirstProperty(element, "category")?.GetString();

    internal static double? FindFirstConfidence(JsonElement element)
    {
        var value = FindFirstProperty(element, "confidence");
        return value is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;
    }

    private static JsonElement? FindFirstProperty(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return property.Value;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    var nested = FindFirstProperty(property.Value, propertyName);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindFirstProperty(item, propertyName);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                break;
        }

        return null;
    }

    private static string ExtractError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            return error.ToString();
        }

        return root.ToString();
    }
}
