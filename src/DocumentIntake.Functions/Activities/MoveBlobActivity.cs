using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Services;
using Microsoft.Azure.Functions.Worker;

namespace DocumentIntake.Functions.Activities;

public sealed class MoveBlobActivity
{
    public const string Name = nameof(MoveBlobActivity);

    private readonly IBlobRouter _blobs;

    public MoveBlobActivity(IBlobRouter blobs) => _blobs = blobs;

    [Function(Name)]
    public Task<MoveBlobResult> RunAsync([ActivityTrigger] MoveBlobRequest request)
        => _blobs.MoveAsync(request);
}

/// <summary>
/// Resolves the URL a blob will have in a container without touching storage, so the
/// Dataverse record can be written before the blob is archived.
/// </summary>
public sealed class ResolveBlobUrlActivity
{
    public const string Name = nameof(ResolveBlobUrlActivity);

    private readonly IBlobRouter _blobs;

    public ResolveBlobUrlActivity(IBlobRouter blobs) => _blobs = blobs;

    [Function(Name)]
    public string RunAsync([ActivityTrigger] BlobReference reference)
        => _blobs.GetBlobUri(reference).ToString();
}
