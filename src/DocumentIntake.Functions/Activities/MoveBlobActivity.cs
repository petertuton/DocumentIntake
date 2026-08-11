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
