using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentIntake.Functions.Services;

/// <summary>
/// Posts the mapped form to the Dataverse Web API. The target environment, entity set, and
/// (for <see cref="DataverseAuthMode.ClientSecret"/>) app registration are all resolved per
/// document classification via <see cref="DataverseOptions.Resolve"/>, since different form
/// types can land in different Dataverse environments. When <see cref="DataverseOptions.Enabled"/>
/// is false the call is a no-op, which keeps the rest of the pipeline runnable before the auth
/// decision is made.
/// </summary>
public sealed class DataverseClient : IDataverseClient
{
    public const string HttpClientName = "dataverse";

    private const int MaxSourceFieldLength = 100;

    private readonly HttpClient _http;
    private readonly DataverseOptions _options;
    private readonly DataverseTokenCredentialFactory _credentialFactory;
    private readonly ILogger<DataverseClient> _logger;

    public DataverseClient(
        HttpClient http,
        IOptions<DataverseOptions> options,
        DataverseTokenCredentialFactory credentialFactory,
        ILogger<DataverseClient> logger)
    {
        _http = http;
        _options = options.Value;
        _credentialFactory = credentialFactory;
        _logger = logger;
    }

    public async Task<string?> CreateRecordAsync(MappedForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (!_options.Enabled)
        {
            // Return the payload instead of a record id so the dry-run body shows up in Durable Functions Monitor.
            var preview = JsonSerializer.Serialize(BuildRecord(form));
            _logger.LogWarning(
                "Dataverse integration is disabled; skipping create for {BlobName}. Payload: {Payload}",
                form.SourceBlobName,
                preview);
            return preview;
        }

        var target = _options.Resolve(form.FormType);

        var requestUri = string.Create(
            CultureInfo.InvariantCulture,
            $"{target.EnvironmentUrl.TrimEnd('/')}/api/data/{_options.ApiVersion}/{target.EntitySetName}");

        var record = BuildRecord(form);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(record),
        };
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("Prefer", "return=representation");

        var credential = _credentialFactory.Create(_options, target);
        var scope = $"{target.EnvironmentUrl.TrimEnd('/')}/.default";
        var token = await credential
            .GetTokenAsync(new TokenRequestContext([scope]), cancellationToken)
            .ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

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

    public async Task<string?> GetRecordAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Dataverse integration is disabled.");
        }

        var target = _options.Resolve(null);

        var requestUri = string.Create(
            CultureInfo.InvariantCulture,
            $"{target.EnvironmentUrl.TrimEnd('/')}/api/data/{_options.ApiVersion}/{target.EntitySetName}({recordId:D})");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");

        var credential = _credentialFactory.Create(_options, target);
        var scope = $"{target.EnvironmentUrl.TrimEnd('/')}/.default";
        var token = await credential
            .GetTokenAsync(new TokenRequestContext([scope]), cancellationToken)
            .ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Dataverse read failed with HTTP {(int)response.StatusCode}: {body}");
        }

        return body;
    }

    internal static Dictionary<string, object?> BuildRecord(MappedForm form)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cr417_primarycolumn"] = form.CompletedBlobUrl
            // ["new_formtype"] = form.FormType,
            // ["new_sourceblobname"] = form.SourceBlobName,
            // ["new_completedbloburl"] = form.CompletedBlobUrl,
            // ["new_processedutc"] = form.ProcessedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            // ["new_classificationconfidence"] = form.ClassificationConfidence,
            // ["new_extractionjson"] = JsonSerializer.Serialize(form.Fields),
        };

        foreach (var field in form.Fields)
        {
            record[field.Column] = ConvertValue(field);
            record[$"{field.Column}confidence"] = field.Confidence;
            record[$"{field.Column}source"] = FormatSource(field.BoundingBox);
        }

        return record;
    }

    private static object? ConvertValue(FieldValue field)
    {
        if (field.Value is null)
        {
            return null;
        }

        return field.Type.ToLowerInvariant() switch
        {
            "boolean" => bool.Parse(field.Value),
            "integer" => long.Parse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            "number" => decimal.Parse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture),
            "date" => ParseDateTime(field.Value),
            _ => field.Value,
        };
    }

    private static DateTimeOffset ParseDateTime(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime))
        {
            return dateTime;
        }

        throw new FormatException($"The extracted date '{value}' is not a valid date.");
    }

    internal static string? FormatSource(BoundingBox? boundingBox)
    {
        if (boundingBox is null)
        {
            return null;
        }

        var source = FormatSource(boundingBox, "G17");
        if (source.Length <= MaxSourceFieldLength)
        {
            return source;
        }

        for (var decimalPlaces = 6; decimalPlaces >= 0; decimalPlaces--)
        {
            var coordinateFormat = decimalPlaces == 0 ? "0" : $"0.{new string('#', decimalPlaces)}";
            source = FormatSource(boundingBox, coordinateFormat);
            if (source.Length <= MaxSourceFieldLength)
            {
                return source;
            }
        }

        return FormatSource(boundingBox, "G6");
    }

    private static string FormatSource(BoundingBox boundingBox, string coordinateFormat)
    {
        var coordinates = string.Join(
            ",",
            boundingBox.Polygon.Select(value => value.ToString(coordinateFormat, CultureInfo.InvariantCulture)));

        return $"D({boundingBox.Page},{coordinates})";
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
