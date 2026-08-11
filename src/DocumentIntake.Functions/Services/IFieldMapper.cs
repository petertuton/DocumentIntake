using DocumentIntake.Functions.Models;

namespace DocumentIntake.Functions.Services;

public interface IFieldMapper
{
    /// <summary>
    /// Projects a Content Understanding analyze payload onto the form's column names,
    /// capturing each field's value, confidence, and position on the page.
    /// </summary>
    IReadOnlyList<FieldValue> Map(string analyzePayload);
}
