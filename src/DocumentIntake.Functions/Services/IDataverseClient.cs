using DocumentIntake.Functions.Models;

namespace DocumentIntake.Functions.Services;

/// <summary>
/// Sends the mapped form to the target system. The auth strategy is deliberately
/// isolated behind this interface while it is being finalised.
/// </summary>
public interface IDataverseClient
{
    Task<string?> CreateRecordAsync(MappedForm form, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a single record as raw Web API JSON, or null when it does not exist.
    /// </summary>
    Task<string?> GetRecordAsync(Guid recordId, CancellationToken cancellationToken = default);
}
