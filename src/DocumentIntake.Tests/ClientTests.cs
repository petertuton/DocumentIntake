using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Options;
using DocumentIntake.Functions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocumentIntake.Tests;

public sealed class AnalysisResultTests
{
    [Fact]
    public void Serialize_EmbedsJsonPayload_InsteadOfEscapingIt()
    {
        var result = new AnalysisResult(AnalysisStatus.Succeeded, """{"contents":[{"category":"hipp-application"}]}""");

        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("\\\"contents\\\"", json, StringComparison.Ordinal);
        Assert.Contains("\"payload\":{\"contents\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_KeepsNonJsonPayload_AsPlainString()
    {
        var result = new AnalysisResult(AnalysisStatus.Failed, "HTTP 500: server error");

        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"payload\":\"HTTP 500: server error\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_DeserializesEmbeddedJsonPayload_BackToRawJsonString()
    {
        var original = new AnalysisResult(AnalysisStatus.Succeeded, """{"contents":[{"category":"hipp-application"}]}""");

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<AnalysisResult>(json);

        Assert.NotNull(roundTripped);
        using var payloadDocument = JsonDocument.Parse(roundTripped!.Payload!);
        Assert.Equal("hipp-application", payloadDocument.RootElement.GetProperty("contents")[0].GetProperty("category").GetString());
    }
}

public sealed class ContentUnderstandingClientTests
{
    [Theory]
    [InlineData("hipp-application-virginia", 0.9, true)]
    [InlineData("hipp-application-nevada", 0.9, true)]
    [InlineData("HIPP-APPLICATION-NEVADA", 0.6, true)]
    [InlineData("hipp-application-nevada", 0.59, false)]
    [InlineData("other", 0.99, false)]
    [InlineData("hipp-application-unknown", 0.99, false)]
    public void GivenMultipleStates_WhenParsingClassification_AppliesAllowlistAndConfidence(
        string category, double confidence, bool expectedKnown)
    {
        var options = new ContentUnderstandingOptions
        {
            KnownFormCategories = ["hipp-application-virginia", "hipp-application-nevada"],
        };
        using var http = new HttpClient();
        var client = new ContentUnderstandingClient(http, Options.Create(options), NullLogger<ContentUnderstandingClient>.Instance);
        var payload = JsonSerializer.Serialize(new { contents = new[] { new { category, confidence } } });

        var result = client.ParseClassification(payload);

        Assert.Equal(expectedKnown, result.IsKnownForm);
        Assert.Equal(category, result.FormType);
    }

    [Theory]
    [InlineData("hipp-application-virginia", true)]
    [InlineData("hipp-application-nevada", false)]
    [InlineData("other", false)]
    public void GivenDefaultOptions_WhenParsingClassification_DefaultsToVirginia(string category, bool expectedKnown)
    {
        using var http = new HttpClient();
        var client = new ContentUnderstandingClient(
            http, Options.Create(new ContentUnderstandingOptions()), NullLogger<ContentUnderstandingClient>.Instance);
        var payload = JsonSerializer.Serialize(new { category, confidence = 0.9 });

        Assert.Equal(expectedKnown, client.ParseClassification(payload).IsKnownForm);
    }

    [Fact]
    public void GivenLegacyCategorySetting_WhenParsingClassification_AcceptsConfiguredCategory()
    {
        using var http = new HttpClient();
        var client = new ContentUnderstandingClient(
            http, Options.Create(new ContentUnderstandingOptions { KnownFormCategory = "hipp-application" }),
            NullLogger<ContentUnderstandingClient>.Instance);

        Assert.True(client.ParseClassification("""{"category":"hipp-application","confidence":0.9}""").IsKnownForm);
    }

    [Fact]
    public void GivenExplicitCategoryList_WhenParsingClassification_DoesNotAlsoAcceptFallback()
    {
        using var http = new HttpClient();
        var client = new ContentUnderstandingClient(
            http, Options.Create(new ContentUnderstandingOptions { KnownFormCategories = ["hipp-application-nevada"] }),
            NullLogger<ContentUnderstandingClient>.Instance);

        Assert.False(client.ParseClassification("""{"category":"hipp-application-virginia","confidence":0.9}""").IsKnownForm);
    }

    [Fact]
    public void GivenIndexedCategorySettings_WhenBindingOptions_AcceptsBothStates()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ContentUnderstanding:KnownFormCategories:0"] = "hipp-application-virginia",
            ["ContentUnderstanding:KnownFormCategories:1"] = "hipp-application-nevada",
        }).Build();
        var options = configuration.GetSection(ContentUnderstandingOptions.SectionName).Get<ContentUnderstandingOptions>()!;
        using var http = new HttpClient();
        var client = new ContentUnderstandingClient(http, Options.Create(options), NullLogger<ContentUnderstandingClient>.Instance);

        Assert.Equal(2, options.KnownFormCategories.Count);
        Assert.True(client.ParseClassification("""{"category":"hipp-application-virginia","confidence":0.9}""").IsKnownForm);
        Assert.True(client.ParseClassification("""{"category":"hipp-application-nevada","confidence":0.9}""").IsKnownForm);
    }

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

    [Fact]
    public void ExtractResultPayload_ReturnsResultNode_WhenPresent()
    {
        const string body = """
        {
          "status": "Succeeded",
          "result": { "contents": [ { "fields": { "claimNumber": { "valueString": "CLM-1" } } } ] }
        }
        """;

        using var document = JsonDocument.Parse(body);
        var payload = ContentUnderstandingClient.ExtractResultPayload(document.RootElement, body);

        using var result = JsonDocument.Parse(payload);
        Assert.True(result.RootElement.TryGetProperty("contents", out _));
        Assert.False(result.RootElement.TryGetProperty("status", out _));
    }

    [Fact]
    public void ExtractResultPayload_RetainsSegmentCategory_ForClassifierResults()
    {
        // Real Content Understanding classifier responses put "category" inside "segments", not
        // directly on the content entry - dropping "segments" during trimming breaks classification.
        const string body = """
        {
          "status": "Succeeded",
          "result": {
            "contents": [
              {
                "path": "input1",
                "markdown": "large ocr text that should be dropped",
                "segments": [ { "segmentId": "segment1", "category": "hipp-application" } ]
              }
            ]
          }
        }
        """;

        using var document = JsonDocument.Parse(body);
        var payload = ContentUnderstandingClient.ExtractResultPayload(document.RootElement, body);

        Assert.Equal("hipp-application", ContentUnderstandingClient.FindFirstCategory(JsonDocument.Parse(payload).RootElement));
        Assert.DoesNotContain("large ocr text", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractResultPayload_FallsBackToRawBody_WhenResultMissing()
    {
        const string body = """{ "status": "Succeeded" }""";

        using var document = JsonDocument.Parse(body);
        var payload = ContentUnderstandingClient.ExtractResultPayload(document.RootElement, body);

        Assert.Equal(body, payload);
    }

    [Fact]
    public void ExtractResultPayload_DropsSpans_ButKeepsTypeValueConfidenceAndSource()
    {
        const string body = """
        {
          "status": "Succeeded",
          "result": {
            "contents": [
              {
                "fields": {
                  "HippNumber": {
                    "type": "integer",
                    "valueInteger": 70004,
                    "spans": [ { "offset": 3209, "length": 5 } ],
                    "confidence": 0.709,
                    "source": "D(2,4.5758,1.4395,4.9470,1.4400,4.9470,1.5363,4.5758,1.5334)"
                  }
                }
              }
            ]
          }
        }
        """;

        using var document = JsonDocument.Parse(body);
        var payload = ContentUnderstandingClient.ExtractResultPayload(document.RootElement, body);

        Assert.DoesNotContain("spans", payload, StringComparison.Ordinal);
        Assert.Contains('\n', payload);

        using var result = JsonDocument.Parse(payload);
        var field = result.RootElement.GetProperty("contents")[0].GetProperty("fields").GetProperty("HippNumber");
        Assert.Equal("integer", field.GetProperty("type").GetString());
        Assert.Equal(70004, field.GetProperty("valueInteger").GetInt32());
        Assert.Equal(0.709, field.GetProperty("confidence").GetDouble(), 3);
        Assert.Equal(
            "D(2,4.5758,1.4395,4.9470,1.4400,4.9470,1.5363,4.5758,1.5334)",
            field.GetProperty("source").GetString());
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
                AnalyzePayload = """
                {
                    "contents": [
                        {
                            "fields": {
                                "HippNumber": {
                                    "type": "integer",
                                    "valueInteger": 70004,
                                    "confidence": 0.709,
                                    "source": "D(2,4.5758,1.4395,4.9470,1.4400,4.9470,1.5363,4.5758,1.5334)"
                                }
                            }
                        }
                    ]
                }
                """,
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

        Assert.Equal(9, record.Count);
        Assert.True(record.TryGetValue("cr417_extractedjson", out var extractedJson));
        var contents = JsonSerializer.Deserialize<JsonElement>(Assert.IsType<string>(extractedJson));
        Assert.Equal(JsonValueKind.Array, contents.ValueKind);
        Assert.Equal(1, contents.GetArrayLength());
        Assert.DoesNotContain("new_formtype", record.Keys);
        Assert.DoesNotContain("new_extractionjson", record.Keys);
    }

    [Theory]
    [InlineData(
        "AAMkADFkZjc3YjhlLWRjOWEtNDk2Ny05M2FlLTM0NjY3MjdlMjg2YQBGAAAAAADoAhreGH-IRLLACKD-cIcVBwDphItCyIXzT4YFBFBvJX8JAAAAAAEMAADphItCyIXzT4YFBFBvJX8JAAGp1AGIAAA=_HIPP_Synthetic_Sample_01.pd",
        "https://github.com/melody-N07/Conduent-HIPP/blob/main/artifacts/samples/HIPP_Synthetic_Sample_01.pdf")]
    [InlineData("HIPP_Synthetic_Sample_07.pdf", "https://github.com/melody-N07/Conduent-HIPP/blob/main/artifacts/samples/HIPP_Synthetic_Sample_07.pdf")]
    public void BuildSampleGitHubUrl_ExtractsSampleIdFromBlobName(string sourceBlobName, string expectedUrl)
    {
        Assert.Equal(expectedUrl, DataverseClient.BuildSampleGitHubUrl(sourceBlobName));
    }

    [Fact]
    public void BuildSampleGitHubUrl_ReturnsNullWhenBlobNameDoesNotMatchSamplePattern()
    {
        Assert.Null(DataverseClient.BuildSampleGitHubUrl("sample-hipp-application.pdf"));
    }

    [Fact]
    public void BuildExtractedJson_ReturnsArrayFromEnvelopePayload()
    {
        var payload = """
        {
          "contents": [
            { "category": "hipp-application" },
            { "category": "unknown" }
          ]
        }
        """;

        var json = DataverseClient.BuildExtractedJson(payload);
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        Assert.Equal(2, element.GetArrayLength());
    }

    [Fact]
    public void BuildExtractedJson_ReducesSourcePrecisionBeforeDroppingEntries()
    {
        const string source = "D(1,0.123456789,1.234567891,2.345678912,3.456789123,4.567891234,5.678912345,6.789123456,7.891234567)";
        var payload = BuildPayload(entryCount: 4, source: source);
        var unbounded = DataverseClient.BuildExtractedJson(payload, maxLength: int.MaxValue, minDecimalPlaces: 2);
        var maxLength = unbounded.Length - 2;

        var json = DataverseClient.BuildExtractedJson(payload, maxLength, minDecimalPlaces: 2);
        var contents = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(4, contents.GetArrayLength());

        var reducedSource = contents[0].GetProperty("fields").GetProperty("claim").GetProperty("source").GetString();
        Assert.NotNull(reducedSource);
        Assert.NotEqual(source, reducedSource);
        Assert.Contains("0.123457", reducedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtractedJson_DropsEntriesOnlyAfterSourcePrecisionReduction()
    {
        var payload = BuildPayload(entryCount: 30, source: "D(1,0.123456789,1.234567891,2.345678912,3.456789123,4.567891234,5.678912345,6.789123456,7.891234567)");

        var json = DataverseClient.BuildExtractedJson(payload, maxLength: 1200, minDecimalPlaces: 2);
        var contents = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.True(contents.GetArrayLength() < 30);
        var source = contents[0].GetProperty("fields").GetProperty("claim").GetProperty("source").GetString();
        Assert.NotNull(source);
        Assert.Contains("0.12", source, StringComparison.Ordinal);
        Assert.DoesNotContain("0.123", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtractedJson_ReturnsEmptyArrayForMalformedPayload()
    {
        Assert.Equal("[]", DataverseClient.BuildExtractedJson("not json"));
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
    public void BuildRecord_EmitsDataverseNativeTypes()
    {
        var form = CreateForm() with
        {
            Fields =
            [
                new FieldValue("cr417_field2", "2024-02-03", 0.9, null, "date"),
                new FieldValue("cr417_field3", "true", 0.9, null, "boolean"),
                new FieldValue("cr417_field4", "12345", 0.9, null, "integer"),
            ],
        };

        var record = DataverseClient.BuildRecord(form);

        Assert.IsType<DateTimeOffset>(record["cr417_field2"]);
        Assert.Equal(true, record["cr417_field3"]);
        Assert.Equal(12345L, record["cr417_field4"]);
    }

    [Fact]
    public void FormatSource_ReturnsNullWithoutBoundingBox()
    {
        Assert.Null(DataverseClient.FormatSource(null));
    }

    [Fact]
    public void FormatSource_ReducesCoordinatePrecisionWhenValueExceedsDataverseLimit()
    {
        var box = new BoundingBox(
            12,
            [
                0.12345678901234567,
                1.9876543210987654,
                2.1234567890123457,
                1.9876543210987654,
                2.1234567890123457,
                3.9876543210987654,
                0.12345678901234567,
                3.9876543210987654,
            ]);

        var source = DataverseClient.FormatSource(box);

        Assert.NotNull(source);
        Assert.True(source!.Length <= 100);
        Assert.StartsWith("D(12,", source, StringComparison.Ordinal);
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

    private static string BuildPayload(int entryCount, string source)
    {
        JsonArray contents = [];

        for (var i = 0; i < entryCount; i++)
        {
            contents.Add(new JsonObject
            {
                ["fields"] = new JsonObject
                {
                    ["claim"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["valueString"] = "ok",
                        ["source"] = source,
                    },
                },
            });
        }

        return new JsonObject { ["contents"] = contents }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
