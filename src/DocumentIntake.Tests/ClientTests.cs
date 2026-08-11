using System.Text.Json;
using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Services;

namespace DocumentIntake.Tests;

public sealed class ContentUnderstandingClientTests
{
    [Theory]
    [InlineData("https://ep.services.ai.azure.com/contentunderstanding/analyzerResults/abc-123?api-version=2025-11-01", "abc-123")]
    [InlineData("https://ep/contentunderstanding/analyzerResults/abc-123", "abc-123")]
    [InlineData("https://ep/contentunderstanding/analyzerResults/abc-123/", "abc-123")]
    public void ExtractOperationId_TakesTheFinalPathSegment(string location, string expected)
    {
        Assert.Equal(expected, ContentUnderstandingClient.ExtractOperationId(location));
    }

    [Fact]
    public void FindFirstCategory_LocatesCategoryAtAnyDepth()
    {
        using var document = JsonDocument.Parse("""
        {
          "status": "Succeeded",
          "result": { "contents": [ { "category": "known-form", "confidence": 0.88 } ] }
        }
        """);

        Assert.Equal("known-form", ContentUnderstandingClient.FindFirstCategory(document.RootElement));
        Assert.Equal(0.88, ContentUnderstandingClient.FindFirstConfidence(document.RootElement)!.Value, 3);
    }

    [Fact]
    public void FindFirstCategory_ReturnsNullWhenAbsent()
    {
        using var document = JsonDocument.Parse("""{ "status": "Running" }""");

        Assert.Null(ContentUnderstandingClient.FindFirstCategory(document.RootElement));
        Assert.Null(ContentUnderstandingClient.FindFirstConfidence(document.RootElement));
    }
}

public sealed class DataverseClientTests
{
    private static MappedForm CreateForm() => new()
    {
        FormType = "known-form",
        SourceBlobName = "invoice.pdf",
        CompletedBlobUrl = "https://acct.blob.core.windows.net/completed/invoice.pdf",
        ProcessedUtc = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero),
        ClassificationConfidence = 0.91,
        Fields =
        [
            new FieldValue("new_claimnumber", "CLM-1", 0.95, new BoundingBox(1, [0, 0, 1, 0, 1, 1, 0, 1])),
            new FieldValue("new_totalamount", "100.5", 0.7, null),
        ],
    };

    [Fact]
    public void BuildRecord_IncludesEnvelopeColumns()
    {
        var record = DataverseClient.BuildRecord(CreateForm());

        Assert.Equal("known-form", record["new_formtype"]);
        Assert.Equal("invoice.pdf", record["new_sourceblobname"]);
        Assert.Equal("https://acct.blob.core.windows.net/completed/invoice.pdf", record["new_completedbloburl"]);
        Assert.Equal(0.91, record["new_classificationconfidence"]);
        Assert.Equal("2024-05-06T07:08:09.0000000Z", record["new_processedutc"]);
    }

    [Fact]
    public void BuildRecord_FlattensEachFieldOntoItsColumn()
    {
        var record = DataverseClient.BuildRecord(CreateForm());

        Assert.Equal("CLM-1", record["new_claimnumber"]);
        Assert.Equal("100.5", record["new_totalamount"]);
    }

    [Fact]
    public void BuildRecord_RetainsFullExtractionWithCoordinatesAndConfidence()
    {
        var record = DataverseClient.BuildRecord(CreateForm());

        var json = Assert.IsType<string>(record["new_extractionjson"]);
        using var document = JsonDocument.Parse(json);

        var first = document.RootElement[0];
        Assert.Equal("new_claimnumber", first.GetProperty("column").GetString());
        Assert.Equal(0.95, first.GetProperty("confidence").GetDouble(), 3);
        Assert.Equal(1, first.GetProperty("boundingBox").GetProperty("page").GetInt32());
        Assert.Equal(8, first.GetProperty("boundingBox").GetProperty("polygon").GetArrayLength());
    }

    [Fact]
    public void ExtractRecordId_ReadsTheEntityGuid()
    {
        var id = Guid.NewGuid();
        var body = $$"""{ "@odata.etag": "W/\"1\"", "new_documentintakeid": "{{id}}" }""";

        Assert.Equal(id.ToString(), DataverseClient.ExtractRecordId(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{ "name": "no id here" }""")]
    [InlineData("""{ "new_id": "not-a-guid" }""")]
    public void ExtractRecordId_ReturnsNullWhenNoGuidIsPresent(string body)
    {
        Assert.Null(DataverseClient.ExtractRecordId(body));
    }
}
