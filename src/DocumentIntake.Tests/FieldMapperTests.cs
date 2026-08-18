using System.Text.Json;
using DocumentIntake.Functions.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocumentIntake.Tests;

public sealed class FieldMapperTests
{
    private static readonly Dictionary<string, string> Columns = new(StringComparer.Ordinal)
    {
        ["ClaimNumber"] = "new_claimnumber",
        ["TotalAmount"] = "new_totalamount",
        ["IsUrgent"] = "new_isurgent",
    };

    private static FieldMapper CreateMapper() =>
        new(Columns, NullLogger<FieldMapper>.Instance);

    [Fact]
    public void Map_ProjectsMappedFieldsWithConfidenceAndCoordinates()
    {
        const string payload = """
        {
          "result": {
            "contents": [
              {
                "fields": {
                  "ClaimNumber": {
                    "type": "string",
                    "valueString": "CLM-4471",
                    "confidence": 0.94,
                    "source": "D(1,0.5,1.0,2.5,1.0,2.5,1.4,0.5,1.4)"
                  },
                  "TotalAmount": {
                    "type": "number",
                    "valueNumber": 1250.75,
                    "confidence": 0.81
                  }
                }
              }
            ]
          }
        }
        """;

        var mapped = CreateMapper().Map(payload);

        Assert.Equal(2, mapped.Count);

        var claim = mapped.Single(f => f.Column == "new_claimnumber");
        Assert.Equal("CLM-4471", claim.Value);
        Assert.Equal(0.94, claim.Confidence, 3);
        Assert.NotNull(claim.BoundingBox);
        Assert.Equal(1, claim.BoundingBox!.Page);
        Assert.Equal(8, claim.BoundingBox.Polygon.Count);

        var total = mapped.Single(f => f.Column == "new_totalamount");
        Assert.Equal("1250.75", total.Value);
        Assert.Null(total.BoundingBox);
    }

    [Fact]
    public void Map_FlattensNestedContentUnderstandingObjects()
    {
        var columns = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ApplicationId"] = "hipp_applicationid",
            ["Policyholder.FirstName"] = "hipp_policyholderfirstname",
        };
        var mapper = new FieldMapper(columns, NullLogger<FieldMapper>.Instance);
        const string payload = """
        {
          "result": {
            "contents": [{
              "fields": {
                "ApplicationId": { "valueString": "SYN-2026-0001", "confidence": 0.82 },
                "Policyholder": {
                  "type": "object",
                  "valueObject": {
                    "FirstName": {
                      "valueString": "Jordan",
                      "confidence": 0.969,
                      "source": "D(2,4.5,2.8,4.9,2.8,4.9,3.0,4.5,3.0)"
                    }
                  }
                }
              }
            }]
          }
        }
        """;

        var mapped = mapper.Map(payload);

        Assert.Equal("SYN-2026-0001", mapped.Single(f => f.Column == "hipp_applicationid").Value);
        var firstName = mapped.Single(f => f.Column == "hipp_policyholderfirstname");
        Assert.Equal("Jordan", firstName.Value);
        Assert.Equal(0.969, firstName.Confidence, 3);
        Assert.NotNull(firstName.BoundingBox);
    }

    [Fact]
    public void Map_IgnoresFieldsWithNoColumnMapping()
    {
        const string payload = """
        { "fields": { "Unmapped": { "valueString": "x", "confidence": 1.0 } } }
        """;

        Assert.Empty(CreateMapper().Map(payload));
    }

    [Fact]
    public void Map_ReturnsEmptyWhenPayloadHasNoFields()
    {
        Assert.Empty(CreateMapper().Map("""{ "status": "Succeeded" }"""));
    }

    [Fact]
    public void Map_ThrowsOnBlankPayload()
    {
        Assert.Throws<ArgumentException>(() => CreateMapper().Map("  "));
    }

    [Theory]
    [InlineData("""{ "valueBoolean": true }""", "true")]
    [InlineData("""{ "valueBoolean": false }""", "false")]
    [InlineData("""{ "valueDate": "2024-02-03" }""", "2024-02-03")]
    [InlineData("""{ "valueNumber": 12 }""", "12")]
    [InlineData("""{ "confidence": 0.5 }""", null)]
    public void ExtractValue_HandlesEachValueShape(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, FieldMapper.ExtractValue(document.RootElement));
    }

    [Fact]
    public void ExtractConfidence_DefaultsToZeroWhenAbsent()
    {
        using var document = JsonDocument.Parse("""{ "valueString": "x" }""");
        Assert.Equal(0d, FieldMapper.ExtractConfidence(document.RootElement));
    }

    [Fact]
    public void ParseSource_ReadsPageAndPolygon()
    {
        var box = FieldMapper.ParseSource("D(3,1.0,2.0,3.0,2.0,3.0,4.0,1.0,4.0)");

        Assert.NotNull(box);
        Assert.Equal(3, box!.Page);
        Assert.Equal(new double[] { 1, 2, 3, 2, 3, 4, 1, 4 }, box.Polygon);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D")]
    [InlineData("D(1)")]
    [InlineData("D(notapage,1.0,2.0)")]
    public void ParseSource_ReturnsNullForUnusableInput(string? source)
    {
        Assert.Null(FieldMapper.ParseSource(source));
    }
}
