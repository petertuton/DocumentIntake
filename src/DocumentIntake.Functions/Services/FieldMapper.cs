using System.Globalization;
using System.Text.Json;
using DocumentIntake.Functions.Models;
using Microsoft.Extensions.Logging;

namespace DocumentIntake.Functions.Services;

/// <summary>
/// Maps analyzer output onto the form's column names using a field-name to column-name
/// dictionary loaded from <c>analyzers/contentunderstanding/field-map.json</c>.
/// </summary>
public sealed class FieldMapper : IFieldMapper
{
    private readonly IReadOnlyDictionary<string, string> _columns;
    private readonly ILogger<FieldMapper> _logger;

    public FieldMapper(IReadOnlyDictionary<string, string> columns, ILogger<FieldMapper> logger)
    {
        _columns = columns;
        _logger = logger;
    }

    public IReadOnlyList<FieldValue> Map(string analyzePayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(analyzePayload);

        using var document = JsonDocument.Parse(analyzePayload);

        if (!TryFindFields(document.RootElement, out var fields))
        {
            _logger.LogWarning("Analyze payload contained no 'fields' object.");
            return [];
        }

        var mapped = new List<FieldValue>(_columns.Count);

        foreach (var field in fields.EnumerateObject())
        {
            if (!_columns.TryGetValue(field.Name, out var column))
            {
                _logger.LogDebug("Skipping unmapped field {FieldName}.", field.Name);
                continue;
            }

            mapped.Add(new FieldValue(
                column,
                ExtractValue(field.Value),
                ExtractConfidence(field.Value),
                ExtractBoundingBox(field.Value)));
        }

        return mapped;
    }

    internal static bool TryFindFields(JsonElement element, out JsonElement fields)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("fields", out var direct) && direct.ValueKind == JsonValueKind.Object)
                {
                    fields = direct;
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindFields(property.Value, out fields))
                    {
                        return true;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindFields(item, out fields))
                    {
                        return true;
                    }
                }

                break;
        }

        fields = default;
        return false;
    }

    internal static string? ExtractValue(JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.Object)
        {
            return field.ValueKind == JsonValueKind.Null ? null : field.ToString();
        }

        foreach (var property in field.EnumerateObject())
        {
            if (!property.Name.StartsWith("value", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetDouble().ToString(CultureInfo.InvariantCulture),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => property.Value.ToString(),
            };
        }

        return null;
    }

    internal static double ExtractConfidence(JsonElement field)
        => field.ValueKind == JsonValueKind.Object
            && field.TryGetProperty("confidence", out var confidence)
            && confidence.ValueKind == JsonValueKind.Number
                ? confidence.GetDouble()
                : 0d;

    internal static BoundingBox? ExtractBoundingBox(JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (field.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String)
        {
            return ParseSource(source.GetString());
        }

        return null;
    }

    /// <summary>
    /// Parses Content Understanding's encoded source string, which has the form
    /// <c>D(pageNumber,x1,y1,x2,y2,x3,y3,x4,y4)</c>.
    /// </summary>
    internal static BoundingBox? ParseSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var open = source.IndexOf('(', StringComparison.Ordinal);
        var close = source.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return null;
        }

        var parts = source[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
        {
            return null;
        }

        var polygon = new List<double>(parts.Length - 1);
        foreach (var part in parts.Skip(1))
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var coordinate))
            {
                polygon.Add(coordinate);
            }
        }

        return polygon.Count == 0 ? null : new BoundingBox(page, polygon);
    }
}
