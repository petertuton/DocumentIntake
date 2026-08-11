using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Services;
using Microsoft.Azure.Functions.Worker;

namespace DocumentIntake.Functions.Activities;

public sealed class SubmitAnalysisActivity
{
    public const string Name = nameof(SubmitAnalysisActivity);

    private readonly IContentUnderstandingClient _client;
    private readonly IBlobRouter _blobs;

    public SubmitAnalysisActivity(IContentUnderstandingClient client, IBlobRouter blobs)
    {
        _client = client;
        _blobs = blobs;
    }

    [Function(Name)]
    public Task<AnalysisOperation> RunAsync([ActivityTrigger] BlobReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return _client.SubmitAnalysisAsync(_blobs.GetBlobUri(reference));
    }
}

public sealed class PollAnalysisActivity
{
    public const string Name = nameof(PollAnalysisActivity);

    private readonly IContentUnderstandingClient _client;

    public PollAnalysisActivity(IContentUnderstandingClient client) => _client = client;

    /// <summary>
    /// One non-blocking status check. The orchestrator owns the wait schedule so that
    /// backoff stays deterministic and replay-safe.
    /// </summary>
    [Function(Name)]
    public Task<AnalysisResult> RunAsync([ActivityTrigger] AnalysisOperation operation)
        => _client.GetAnalysisResultAsync(operation);
}
