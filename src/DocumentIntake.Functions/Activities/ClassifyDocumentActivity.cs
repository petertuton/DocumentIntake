using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Options;
using DocumentIntake.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentIntake.Functions.Activities;

public sealed class ClassifyDocumentActivity
{
    public const string Name = nameof(ClassifyDocumentActivity);

    private readonly IContentUnderstandingClient _client;
    private readonly IBlobRouter _blobs;
    private readonly ContentUnderstandingOptions _options;
    private readonly ILogger<ClassifyDocumentActivity> _logger;

    public ClassifyDocumentActivity(
        IContentUnderstandingClient client,
        IBlobRouter blobs,
        IOptions<ContentUnderstandingOptions> options,
        ILogger<ClassifyDocumentActivity> logger)
    {
        _client = client;
        _blobs = blobs;
        _options = options.Value;
        _logger = logger;
    }

    [Function(Name)]
    public async Task<ClassificationResult> RunAsync([ActivityTrigger] BlobReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var size = await _blobs.GetSizeAsync(reference).ConfigureAwait(false);
        if (size is null)
        {
            throw new InvalidOperationException(
                $"Blob '{reference.BlobName}' no longer exists in '{reference.ContainerName}'.");
        }

        if (size > _options.MaxDocumentSizeBytes)
        {
            throw new DocumentTooLargeException(
                $"Blob '{reference.BlobName}' is {size} bytes, exceeding the {_options.MaxDocumentSizeBytes} byte limit.");
        }

        var uri = _blobs.GetBlobUri(reference);
        var result = await _client.ClassifyAsync(uri).ConfigureAwait(false);

        _logger.LogInformation(
            "Classified {BlobName} as {FormType} (known={IsKnownForm}, confidence={Confidence:F2}).",
            reference.BlobName,
            result.FormType,
            result.IsKnownForm,
            result.Confidence);

        return result;
    }
}

/// <summary>Thrown when a document exceeds the configured size limit; not retried.</summary>
public sealed class DocumentTooLargeException : Exception
{
    public DocumentTooLargeException()
    {
    }

    public DocumentTooLargeException(string message) : base(message)
    {
    }

    public DocumentTooLargeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
