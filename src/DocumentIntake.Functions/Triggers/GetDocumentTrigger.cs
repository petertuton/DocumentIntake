using Azure.Storage.Blobs;
using DocumentIntake.Functions.Options;
using DocumentIntake.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace DocumentIntake.Functions.Triggers;

/// <summary>
/// Serves a processed document to the UI. Storage has public network access disabled, so the
/// bytes cannot be fetched directly from the browser and must be proxied through the app.
/// </summary>
public sealed class GetDocumentTrigger
{
    // Anything outside this set is served as an opaque download so the browser never renders
    // caller-influenced markup (html, svg) from the same origin as the API.
    private static readonly HashSet<string> InlineContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/tiff",
        "text/plain",
    };

    private const string FallbackContentType = "application/octet-stream";

    private readonly IBlobRouter _router;
    private readonly Uri _blobServiceUri;
    private readonly ILogger<GetDocumentTrigger> _logger;

    public GetDocumentTrigger(IBlobRouter router, BlobServiceClient blobServiceClient, ILogger<GetDocumentTrigger> logger)
    {
        ArgumentNullException.ThrowIfNull(blobServiceClient);
        _router = router;
        _blobServiceUri = blobServiceClient.Uri;
        _logger = logger;
    }

    [Function("GetDocument")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "documents/content")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = request.Query["url"].ToString();

        if (!BlobUrlValidator.TryParse(url, _blobServiceUri, Containers.PublicViewable, out var reference, out var error))
        {
            _logger.LogWarning("Rejected document request: {Reason}", error);
            return new BadRequestObjectResult(new { error });
        }

        var content = await _router.DownloadWithPropertiesAsync(reference, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            _logger.LogInformation(
                "Document {BlobName} was not found in {Container}.",
                reference.BlobName,
                reference.ContainerName);
            return new NotFoundResult();
        }

        var contentType = InlineContentTypes.Contains(content.ContentType)
            ? content.ContentType
            : FallbackContentType;

        var headers = request.HttpContext.Response.Headers;
        headers[HeaderNames.ContentDisposition] = BuildContentDisposition(content.FileName);
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.CacheControl] = "private, no-store";

        _logger.LogInformation(
            "Served document {BlobName} from {Container} ({Length} bytes).",
            reference.BlobName,
            reference.ContainerName,
            content.Length);

        return new FileContentResult(content.Content.ToArray(), contentType);
    }

    private static string BuildContentDisposition(string fileName)
    {
        var safe = new string([.. fileName.Where(c => !char.IsControl(c) && c is not ('"' or '\\'))]);
        if (string.IsNullOrWhiteSpace(safe))
        {
            return "inline";
        }

        return $"inline; filename=\"{safe}\"; filename*=UTF-8''{Uri.EscapeDataString(safe)}";
    }
}
