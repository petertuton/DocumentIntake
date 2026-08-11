using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocumentIntake.Functions.Models;
using Microsoft.Extensions.Logging;

namespace DocumentIntake.Functions.Services;

public sealed class BlobRouter : IBlobRouter
{
    private static readonly TimeSpan CopyPollInterval = TimeSpan.FromSeconds(1);
    private const int MaxCopyPolls = 120;

    private readonly BlobServiceClient _client;
    private readonly ILogger<BlobRouter> _logger;

    public BlobRouter(BlobServiceClient client, ILogger<BlobRouter> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<MoveBlobResult> MoveAsync(MoveBlobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = _client.GetBlobContainerClient(request.SourceContainer).GetBlobClient(request.BlobName);
        var destinationContainer = _client.GetBlobContainerClient(request.DestinationContainer);
        var destination = destinationContainer.GetBlobClient(request.BlobName);

        if (!await source.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            // Already moved (for example an at-least-once retry): treat as success
            // when the destination holds the blob, otherwise surface the problem.
            if (await destination.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Blob {BlobName} already present in {Destination}; treating move as complete.",
                    request.BlobName,
                    request.DestinationContainer);

                return new MoveBlobResult(request.DestinationContainer, request.BlobName, destination.Uri.ToString());
            }

            throw new InvalidOperationException(
                $"Blob '{request.BlobName}' was not found in '{request.SourceContainer}' or '{request.DestinationContainer}'.");
        }

        var operation = await destination
            .StartCopyFromUriAsync(source.Uri, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await WaitForCopyAsync(destination, cancellationToken).ConfigureAwait(false);
        _ = operation;

        if (request.MetadataToAdd is { Count: > 0 })
        {
            var properties = await destination.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var metadata = new Dictionary<string, string>(properties.Value.Metadata, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in request.MetadataToAdd)
            {
                metadata[SanitizeMetadataKey(key)] = value;
            }

            await destination.SetMetadataAsync(metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await source.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Moved blob {BlobName} from {Source} to {Destination}.",
            request.BlobName,
            request.SourceContainer,
            request.DestinationContainer);

        return new MoveBlobResult(request.DestinationContainer, request.BlobName, destination.Uri.ToString());
    }

    public async Task<bool> ExistsAsync(BlobReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var blob = _client.GetBlobContainerClient(reference.ContainerName).GetBlobClient(reference.BlobName);
        return await blob.ExistsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> GetSizeAsync(BlobReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var blob = _client.GetBlobContainerClient(reference.ContainerName).GetBlobClient(reference.BlobName);

        try
        {
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return properties.Value.ContentLength;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<BinaryData> DownloadAsync(BlobReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var blob = _client.GetBlobContainerClient(reference.ContainerName).GetBlobClient(reference.BlobName);
        var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return response.Value.Content;
    }

    public Uri GetBlobUri(BlobReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return _client.GetBlobContainerClient(reference.ContainerName).GetBlobClient(reference.BlobName).Uri;
    }

    private static async Task WaitForCopyAsync(BlobClient destination, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxCopyPolls; attempt++)
        {
            var properties = await destination.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            switch (properties.Value.CopyStatus)
            {
                case CopyStatus.Success:
                    return;
                case CopyStatus.Pending:
                    await Task.Delay(CopyPollInterval, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Blob copy to '{destination.Name}' ended with status {properties.Value.CopyStatus}: {properties.Value.CopyStatusDescription}");
            }
        }

        throw new TimeoutException($"Blob copy to '{destination.Name}' did not complete in time.");
    }

    private static string SanitizeMetadataKey(string key)
    {
        // Azure blob metadata keys must be valid C# identifiers.
        var chars = key.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var sanitized = new string(chars);
        return char.IsDigit(sanitized[0]) ? "_" + sanitized : sanitized;
    }
}
