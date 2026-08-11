using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DocumentIntake.Functions.Activities;

/// <summary>Input to the mapping activity: the raw analyze payload plus provenance.</summary>
public sealed record MapFieldsRequest(
    string AnalyzePayload,
    string FormType,
    string SourceBlobName,
    double ClassificationConfidence);

public sealed class MapFieldsActivity
{
    public const string Name = nameof(MapFieldsActivity);

    private readonly IFieldMapper _mapper;
    private readonly ILogger<MapFieldsActivity> _logger;

    public MapFieldsActivity(IFieldMapper mapper, ILogger<MapFieldsActivity> logger)
    {
        _mapper = mapper;
        _logger = logger;
    }

    [Function(Name)]
    public MappedForm Run([ActivityTrigger] MapFieldsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fields = _mapper.Map(request.AnalyzePayload);

        _logger.LogInformation(
            "Mapped {FieldCount} fields for {BlobName}.",
            fields.Count,
            request.SourceBlobName);

        return new MappedForm
        {
            FormType = request.FormType,
            SourceBlobName = request.SourceBlobName,
            ClassificationConfidence = request.ClassificationConfidence,
            ProcessedUtc = DateTimeOffset.UtcNow,
            Fields = fields,
        };
    }
}
