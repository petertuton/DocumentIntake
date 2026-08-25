using DocumentIntake.Functions.Activities;
using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocumentIntake.Tests;

public sealed class MapFieldsActivityTests
{
    [Fact]
    public void Run_PreservesAnalyzePayloadOnMappedForm()
    {
        const string analyzePayload = """
        {
          "contents": [
            {
              "fields": {
                "ClaimNumber": {
                  "type": "string",
                  "valueString": "CLM-1"
                }
              }
            }
          ]
        }
        """;

        var mapper = new StubMapper(
            [
                new FieldValue("new_claimnumber", "CLM-1", 0.95, null),
            ]);
        var activity = new MapFieldsActivity(mapper, NullLogger<MapFieldsActivity>.Instance);

        var result = activity.Run(new MapFieldsRequest(analyzePayload, "hipp-application", "inbox/file.pdf", 0.87));

        Assert.Equal(analyzePayload, result.AnalyzePayload);
    }

    private sealed class StubMapper(IReadOnlyList<FieldValue> fields) : IFieldMapper
    {
        public IReadOnlyList<FieldValue> Map(string analyzePayload) => fields;
    }
}
