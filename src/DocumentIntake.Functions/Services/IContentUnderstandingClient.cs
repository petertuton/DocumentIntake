using DocumentIntake.Functions.Models;

namespace DocumentIntake.Functions.Services;

public interface IContentUnderstandingClient
{
    /// <summary>Runs the classifier over a blob and waits for the (fast) result.</summary>
    Task<ClassificationResult> ClassifyAsync(Uri blobUri, CancellationToken cancellationToken = default);

    /// <summary>Submits the blob to the extraction analyzer; returns the long-running operation handle.</summary>
    Task<AnalysisOperation> SubmitAnalysisAsync(Uri blobUri, CancellationToken cancellationToken = default);

    /// <summary>Performs a single, non-blocking status check of a submitted operation.</summary>
    Task<AnalysisResult> GetAnalysisResultAsync(AnalysisOperation operation, CancellationToken cancellationToken = default);
}
