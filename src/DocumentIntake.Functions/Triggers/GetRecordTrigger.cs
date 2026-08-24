using DocumentIntake.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace DocumentIntake.Functions.Triggers;

/// <summary>
/// Serves a Dataverse record to the UI. The Dataverse credential is configured on the app and
/// must stay server-side, so the browser cannot call the Web API directly.
/// </summary>
public sealed class GetRecordTrigger(IDataverseClient dataverse, ILogger<GetRecordTrigger> logger)
{
    [Function("GetRecord")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "documents/record")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Parsing as a Guid is also what keeps caller input out of the OData resource path.
        if (!Guid.TryParse(request.Query["id"].ToString(), out var recordId))
        {
            return new BadRequestObjectResult(new { error = "A record 'id' query parameter in GUID form is required." });
        }

        string? body;
        try
        {
            body = await dataverse.GetRecordAsync(recordId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            // The upstream message can carry environment detail, so it stays in the logs.
            logger.LogError(exception, "Dataverse read failed for record {RecordId}.", recordId);
            return new ObjectResult(new { error = "Could not read the record from Dataverse." })
            {
                StatusCode = StatusCodes.Status502BadGateway,
            };
        }

        if (body is null)
        {
            logger.LogInformation("Dataverse record {RecordId} was not found.", recordId);
            return new NotFoundResult();
        }

        request.HttpContext.Response.Headers[HeaderNames.CacheControl] = "private, no-store";

        logger.LogInformation("Served Dataverse record {RecordId}.", recordId);
        return new ContentResult
        {
            Content = body,
            ContentType = "application/json",
            StatusCode = StatusCodes.Status200OK,
        };
    }
}
