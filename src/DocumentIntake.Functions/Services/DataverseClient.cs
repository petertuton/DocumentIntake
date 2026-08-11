using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentIntake.Functions.Services;

/// <summary>
/// Posts the mapped form to the Dataverse Web API. Auth is supplied by the configured
/// <see cref="ManagedIdentityAuthHandler"/>; swap that out if an app registration is
/// chosen instead. When <see cref="DataverseOptions.Enabled"/> is false the call is a no-op,
/// which keeps the rest of the pipeline runnable before the auth decision is made.
/// </summary>
public sealed class DataverseClient : IDataverseClient
{
    public const string HttpClientName = "dataverse";

    private readonly HttpClient _http;
    private readonly DataverseOptions _options;
    private readonly ILogger<DataverseClient> _logger;

    public DataverseClient(HttpClient http, IOptions<DataverseOptions> options, ILogger<DataverseClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> CreateRecordAsync(MappedForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (!_options.Enabled)
        {
            _logger.LogWarning(
                "Dataverse integration is disabled; skipping create for {BlobName}. Payload: {Payload}",
                form.SourceBlobName,
                JsonSerializer.Serialize(form));
            return null;
        }

        var requestUri = string.Create(
            CultureInfo.InvariantCulture,
            $"{_options.EnvironmentUrl.TrimEnd('/')}/api/data/{_options.ApiVersion}/{_options.EntitySetName}");

        var record = BuildRecord(form);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(record),
        };
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("Prefer", "return=representation");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Dataverse create failed with HTTP {(int)response.StatusCode}: {body}");
        }

        var recordId = ExtractRecordId(body);
        _logger.LogInformation("Created Dataverse record {RecordId} for {BlobName}.", recordId, form.SourceBlobName);
        return recordId;
    }

    internal static Dictionary<string, object?> BuildRecord(MappedForm form)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["new_formtype"] = form.FormType,
            ["new_sourceblobname"] = form.SourceBlobName,
            ["new_completedbloburl"] = form.CompletedBlobUrl,
            ["new_processedutc"] = form.ProcessedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["new_classificationconfidence"] = form.ClassificationConfidence,

            // The full extraction — values, confidences, and coordinates — is retained
            // so downstream review tooling can highlight low-confidence fields.
            ["new_extractionjson"] = JsonSerializer.Serialize(form.Fields),
        };

        foreach (var field in form.Fields)
        {
            record[field.Column] = field.Value;
        }

        return record;
    }

    internal static string? ExtractRecordId(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.EndsWith("id", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(property.Value.GetString(), out var id))
                {
                    return id.ToString();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
