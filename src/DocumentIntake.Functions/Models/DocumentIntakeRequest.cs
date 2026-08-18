namespace DocumentIntake.Functions.Models;

/// <summary>Input to the orchestration: identifies the blob that arrived in the inbox.</summary>
public sealed record DocumentIntakeRequest(
    string ContainerName,
    string BlobName,
    string? ETag = null,
    long? SizeBytes = null);

/// <summary>Identifies a blob in a specific container.</summary>
public sealed record BlobReference(string ContainerName, string BlobName);

/// <summary>Instruction to copy a blob between containers and delete the source.</summary>
public sealed record MoveBlobRequest(
    string SourceContainer,
    string DestinationContainer,
    string BlobName,
    IDictionary<string, string>? MetadataToAdd = null);

/// <summary>Result of moving a blob, carrying the destination URL.</summary>
public sealed record MoveBlobResult(string DestinationContainer, string BlobName, string Url);

/// <summary>Downloaded blob bytes together with the properties needed to serve them over HTTP.</summary>
public sealed record BlobContent(
    BinaryData Content,
    string ContentType,
    string FileName,
    long Length,
    string? ETag);
