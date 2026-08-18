using System.Diagnostics.CodeAnalysis;
using DocumentIntake.Functions.Models;

namespace DocumentIntake.Functions.Services;

/// <summary>
/// Turns a caller-supplied blob URL into a <see cref="BlobReference"/>, rejecting anything that
/// does not point at the configured storage account and an allowed container. Without this the
/// endpoint would fetch arbitrary URLs on behalf of the caller.
/// </summary>
public static class BlobUrlValidator
{
    /// <summary>
    /// Attempts to resolve <paramref name="url"/> to a blob in one of <paramref name="allowedContainers"/>.
    /// </summary>
    /// <param name="url">The absolute blob URL supplied by the caller.</param>
    /// <param name="blobServiceUri">Base URI of the storage account the app is configured against.</param>
    /// <param name="allowedContainers">Containers the caller is permitted to read from.</param>
    /// <param name="reference">The resolved blob reference when validation succeeds.</param>
    /// <param name="error">A short reason when validation fails.</param>
    /// <returns><see langword="true"/> when the URL is valid and permitted.</returns>
    public static bool TryParse(
        string? url,
        Uri blobServiceUri,
        IReadOnlyCollection<string> allowedContainers,
        [NotNullWhen(true)] out BlobReference? reference,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(blobServiceUri);
        ArgumentNullException.ThrowIfNull(allowedContainers);

        reference = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "A blob url is required.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = "The blob url is not an absolute uri.";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "The blob url must use https.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "The blob url must not contain credentials.";
            return false;
        }

        if (!string.Equals(parsed.Host, blobServiceUri.Host, StringComparison.OrdinalIgnoreCase)
            || parsed.Port != blobServiceUri.Port)
        {
            error = "The blob url does not belong to the configured storage account.";
            return false;
        }

        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            error = "The blob url must include a container and a blob name.";
            return false;
        }

        var container = Uri.UnescapeDataString(segments[0]);
        if (!allowedContainers.Contains(container, StringComparer.OrdinalIgnoreCase))
        {
            error = "The blob url points at a container that cannot be served.";
            return false;
        }

        var nameSegments = segments[1..].Select(Uri.UnescapeDataString).ToArray();
        if (nameSegments.Any(segment => segment is "." or ".."))
        {
            error = "The blob url contains an invalid path segment.";
            return false;
        }

        var blobName = string.Join('/', nameSegments);
        if (string.IsNullOrWhiteSpace(blobName))
        {
            error = "The blob url must include a blob name.";
            return false;
        }

        // The query string is dropped so a caller cannot append a SAS or alter server behaviour.
        reference = new BlobReference(container, blobName);
        error = null;
        return true;
    }
}
