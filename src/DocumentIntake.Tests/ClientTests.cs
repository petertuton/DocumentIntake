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
    public void BuildRecord_ContainsOnlyMappedFieldTriplets()
    {
        var record = DataverseClient.BuildRecord(CreateForm());

        Assert.Equal(7, record.Count);
        Assert.DoesNotContain("new_formtype", record.Keys);
        Assert.DoesNotContain("new_extractionjson", record.Keys);
    }

    [Fact]
    public void BuildRecord_EmitsValueConfidenceAndSourceForEachField()
    {
        var record = DataverseClient.BuildRecord(CreateForm());

        Assert.Equal("CLM-1", record["new_claimnumber"]);
        Assert.Equal(0.95, record["new_claimnumberconfidence"]);
        Assert.Equal("D(1,0,0,1,0,1,1,0,1)", record["new_claimnumbersource"]);

        Assert.Equal("100.5", record["new_totalamount"]);
        Assert.Equal(0.7, record["new_totalamountconfidence"]);
        Assert.Null(record["new_totalamountsource"]);
    }

    [Fact]
    public void FormatSource_ReturnsNullWithoutBoundingBox()
    {
        Assert.Null(DataverseClient.FormatSource(null));
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
