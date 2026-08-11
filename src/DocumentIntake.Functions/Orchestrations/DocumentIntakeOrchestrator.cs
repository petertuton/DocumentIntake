using DocumentIntake.Functions.Activities;
using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DocumentIntake.Functions.Orchestrations;

/// <summary>Final outcome of processing one document.</summary>
public sealed record DocumentIntakeOutcome(
    string BlobName,
    string Disposition,
    string? FinalContainer,
    string? FinalUrl,
    string? DataverseRecordId,
    string? Reason);

public static class DocumentIntakeOrchestrator
{
    public const string Name = nameof(DocumentIntakeOrchestrator);

    /// <summary>Overall budget for a single analyze operation.</summary>
    internal static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(10);

    private static readonly TaskOptions RetryOptions = TaskOptions.FromRetryPolicy(
        new RetryPolicy(
            maxNumberOfAttempts: 4,
            firstRetryInterval: TimeSpan.FromSeconds(5),
            backoffCoefficient: 2.0,
            maxRetryInterval: TimeSpan.FromMinutes(2)));

    [Function(Name)]
    public static async Task<DocumentIntakeOutcome> RunAsync(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.GetInput<DocumentIntakeRequest>()
            ?? throw new InvalidOperationException("Orchestration input was missing.");

        var logger = context.CreateReplaySafeLogger(nameof(DocumentIntakeOrchestrator));

        try
        {
            // 1. Classify while the file is still in the inbox.
            var inboxRef = new BlobReference(Containers.Inbox, request.BlobName);
            var classification = await context.CallActivityAsync<ClassificationResult>(
                ClassifyDocumentActivity.Name,
                inboxRef,
                RetryOptions);

            // 2. Route: unknown documents leave the pipeline here.
            if (!classification.IsKnownForm)
            {
                var ignored = await context.CallActivityAsync<MoveBlobResult>(
                    MoveBlobActivity.Name,
                    new MoveBlobRequest(
                        Containers.Inbox,
                        Containers.Ignored,
                        request.BlobName,
                        BuildMetadata(classification.Reason ?? "Not a known form.")),
                    RetryOptions);

                logger.LogInformation("Ignored {BlobName}: {Reason}", request.BlobName, classification.Reason);

                return new DocumentIntakeOutcome(
                    request.BlobName,
                    "Ignored",
                    ignored.DestinationContainer,
                    ignored.Url,
                    null,
                    classification.Reason);
            }

            var processing = await context.CallActivityAsync<MoveBlobResult>(
                MoveBlobActivity.Name,
                new MoveBlobRequest(Containers.Inbox, Containers.Processing, request.BlobName),
                RetryOptions);

            var processingRef = new BlobReference(processing.DestinationContainer, request.BlobName);

            // 3. Submit for OCR / field extraction.
            var operation = await context.CallActivityAsync<AnalysisOperation>(
                SubmitAnalysisActivity.Name,
                processingRef,
                RetryOptions);

            // 4. Smart poll with deterministic backoff and an overall budget.
            var payload = await PollUntilCompleteAsync(context, operation, PollTimeout, logger);

            // 5. Map the extraction onto the form's column names.
            var mapped = await context.CallActivityAsync<MappedForm>(
                MapFieldsActivity.Name,
                new MapFieldsRequest(
                    payload,
                    classification.FormType ?? "known-form",
                    request.BlobName,
                    classification.Confidence),
                RetryOptions);

            // 6. Archive, capturing the final URL in the payload.
            var completed = await context.CallActivityAsync<MoveBlobResult>(
                MoveBlobActivity.Name,
                new MoveBlobRequest(Containers.Processing, Containers.Completed, request.BlobName),
                RetryOptions);

            var finalForm = mapped with { CompletedBlobUrl = completed.Url };

            // 7. Hand off to Dataverse.
            var recordId = await context.CallActivityAsync<string?>(
                PostToDataverseActivity.Name,
                finalForm,
                RetryOptions);

            logger.LogInformation("Completed {BlobName} as record {RecordId}.", request.BlobName, recordId);

            return new DocumentIntakeOutcome(
                request.BlobName,
                "Completed",
                completed.DestinationContainer,
                completed.Url,
                recordId,
                null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Document intake failed for {BlobName}.", request.BlobName);
            var failed = await MoveToFailedAsync(context, request.BlobName, ex.Message, logger);

            return new DocumentIntakeOutcome(
                request.BlobName,
                "Failed",
                failed?.DestinationContainer,
                failed?.Url,
                null,
                ex.Message);
        }
    }

    private static async Task<string> PollUntilCompleteAsync(
        TaskOrchestrationContext context,
        AnalysisOperation operation,
        TimeSpan timeout,
        ILogger logger)
    {
        var deadline = context.CurrentUtcDateTime.Add(timeout);

        for (var attempt = 0; ; attempt++)
        {
            var delay = PollingSchedule.GetDelay(attempt);
            var wakeAt = context.CurrentUtcDateTime.Add(delay);

            if (wakeAt > deadline)
            {
                throw new TimeoutException(
                    $"Analysis operation '{operation.OperationId}' did not complete within {timeout}.");
            }

            await context.CreateTimer(wakeAt, CancellationToken.None);

            var result = await context.CallActivityAsync<AnalysisResult>(
                PollAnalysisActivity.Name,
                operation,
                RetryOptions);

            switch (result.Status)
            {
                case AnalysisStatus.Succeeded:
                    return result.Payload
                        ?? throw new InvalidOperationException("Analysis succeeded but returned no payload.");

                case AnalysisStatus.Failed:
                    throw new InvalidOperationException(
                        $"Analysis operation '{operation.OperationId}' failed: {result.Error}");

                default:
                    logger.LogInformation(
                        "Analysis {OperationId} still running (poll {Attempt}).",
                        operation.OperationId,
                        attempt + 1);
                    break;
            }
        }
    }

    private static async Task<MoveBlobResult?> MoveToFailedAsync(
        TaskOrchestrationContext context,
        string blobName,
        string reason,
        ILogger logger)
    {
        // The blob may be in the inbox or already in processing depending on where we failed.
        foreach (var source in new[] { Containers.Processing, Containers.Inbox })
        {
            try
            {
                return await context.CallActivityAsync<MoveBlobResult>(
                    MoveBlobActivity.Name,
                    new MoveBlobRequest(source, Containers.Failed, blobName, BuildMetadata(reason)),
                    RetryOptions);
            }
            catch (TaskFailedException ex)
            {
                logger.LogWarning(
                    "Could not move {BlobName} from {Source} to {Failed}: {Message}",
                    blobName,
                    source,
                    Containers.Failed,
                    ex.Message);
            }
        }

        return null;
    }

    private static Dictionary<string, string> BuildMetadata(string reason) => new(StringComparer.Ordinal)
    {
        ["intakeReason"] = Truncate(reason, 1000),
    };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
