using System.Text.Json;
using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Options;
using DocumentIntake.Functions.Orchestrations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace DocumentIntake.Functions.Triggers;

/// <summary>
/// Starts one orchestration per blob created in the inbox container. Event Grid delivers
/// at least once, so the instance id is derived from the blob name and etag: a duplicate
/// delivery simply attaches to the existing instance instead of double-processing.
/// </summary>
public sealed class BlobCreatedTrigger
{
    private readonly ILogger<BlobCreatedTrigger> _logger;

    public BlobCreatedTrigger(ILogger<BlobCreatedTrigger> logger) => _logger = logger;

    [Function(nameof(BlobCreatedTrigger))]
    public async Task RunAsync(
        [EventGridTrigger] BlobCreatedEvent eventGridEvent,
        [DurableClient] DurableTaskClient client)
    {
        ArgumentNullException.ThrowIfNull(eventGridEvent);
        ArgumentNullException.ThrowIfNull(client);

        if (!TryParseBlobUrl(eventGridEvent.Data?.Url, out var containerName, out var blobName))
        {
            _logger.LogWarning("Ignoring event with unparsable blob url {Url}.", eventGridEvent.Data?.Url);
            return;
        }

        if (!string.Equals(containerName, Containers.Inbox, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Ignoring event for container {Container}.", containerName);
            return;
        }

        var request = new DocumentIntakeRequest(
            containerName,
            blobName,
            eventGridEvent.Data?.ETag,
            eventGridEvent.Data?.ContentLength);

        var instanceId = BuildInstanceId(blobName, eventGridEvent.Data?.ETag);

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Orchestration {InstanceId} already exists for {BlobName}; skipping duplicate event.",
                instanceId,
                blobName);
            return;
        }

        await client.ScheduleNewOrchestrationInstanceAsync(
            DocumentIntakeOrchestrator.Name,
            request,
            new StartOrchestrationOptions(instanceId)).ConfigureAwait(false);

        _logger.LogInformation("Started orchestration {InstanceId} for {BlobName}.", instanceId, blobName);
    }

    /// <summary>
    /// Builds a deterministic, storage-safe instance id from the blob identity so that
    /// repeated deliveries of the same event map to the same orchestration.
    /// </summary>
    internal static string BuildInstanceId(string blobName, string? etag)
    {
        var raw = string.IsNullOrEmpty(etag) ? blobName : $"{blobName}|{etag}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return "di-" + Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    internal static bool TryParseBlobUrl(string? url, out string containerName, out string blobName)
    {
        containerName = string.Empty;
        blobName = string.Empty;

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimStart('/');
        var separator = path.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == path.Length - 1)
        {
            return false;
        }

        containerName = Uri.UnescapeDataString(path[..separator]);
        blobName = Uri.UnescapeDataString(path[(separator + 1)..]);
        return true;
    }
}

/// <summary>Minimal projection of the Storage BlobCreated event schema.</summary>
public sealed class BlobCreatedEvent
{
    public string? Id { get; set; }
    public string? EventType { get; set; }
    public string? Subject { get; set; }
    public BlobCreatedEventData? Data { get; set; }
}

public sealed class BlobCreatedEventData
{
    public string? Api { get; set; }
    public string? Url { get; set; }
    public string? ETag { get; set; }
    public string? ContentType { get; set; }
    public long? ContentLength { get; set; }
    public string? BlobType { get; set; }
}
