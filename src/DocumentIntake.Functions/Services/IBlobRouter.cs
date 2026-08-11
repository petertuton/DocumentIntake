using DocumentIntake.Functions.Models;

namespace DocumentIntake.Functions.Services;

public interface IBlobRouter
{
    /// <summary>
    /// Copies a blob to the destination container then deletes the source, so a
    /// failure part-way through never loses the file.
    /// </summary>
    Task<MoveBlobResult> MoveAsync(MoveBlobRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns true when the blob exists in the given container.</summary>
    Task<bool> ExistsAsync(BlobReference reference, CancellationToken cancellationToken = default);

    /// <summary>Gets the size in bytes of a blob, or null when it does not exist.</summary>
    Task<long?> GetSizeAsync(BlobReference reference, CancellationToken cancellationToken = default);

    /// <summary>Downloads the blob content.</summary>
    Task<BinaryData> DownloadAsync(BlobReference reference, CancellationToken cancellationToken = default);

    /// <summary>Absolute URL of a blob (no SAS; access is via managed identity).</summary>
    Uri GetBlobUri(BlobReference reference);
}
