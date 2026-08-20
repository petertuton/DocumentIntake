using Microsoft.Extensions.Options;

namespace DocumentIntake.Functions.Options;

/// <summary>
/// Validates the configuration required by the selected Dataverse authentication mode.
/// </summary>
public sealed class DataverseOptionsValidator : IValidateOptions<DataverseOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, DataverseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        AddRequiredFailure(failures, options.EnvironmentUrl, nameof(options.EnvironmentUrl));
        AddRequiredFailure(failures, options.EntitySetName, nameof(options.EntitySetName));

        if (!Enum.IsDefined(options.AuthMode))
        {
            failures.Add($"{nameof(options.AuthMode)} must be a supported authentication mode.");
        }
        else if (options.AuthMode == DataverseAuthMode.ClientSecret)
        {
            AddRequiredFailure(failures, options.ClientSecretTenantId, nameof(options.ClientSecretTenantId));
            AddRequiredFailure(failures, options.ClientSecretClientId, nameof(options.ClientSecretClientId));
            AddRequiredFailure(failures, options.ClientSecretValue, nameof(options.ClientSecretValue));

            foreach (var mapping in options.FormMappings)
            {
                AddPartialCredentialOverrideFailure(failures, mapping.Classification, mapping);
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void AddPartialCredentialOverrideFailure(List<string> failures, string formType, DataverseFormMapping mapping)
    {
        var filled = 0;
        if (!string.IsNullOrWhiteSpace(mapping.ClientSecretTenantId)) filled++;
        if (!string.IsNullOrWhiteSpace(mapping.ClientSecretClientId)) filled++;
        if (!string.IsNullOrWhiteSpace(mapping.ClientSecretValue)) filled++;

        if (filled is not (0 or 3))
        {
            failures.Add(
                $"{DataverseOptions.SectionName}:FormMappings:{formType} must set ClientSecretTenantId, " +
                "ClientSecretClientId, and ClientSecretValue together, or leave all three blank to use the defaults.");
        }
    }

    private static void AddRequiredFailure(List<string> failures, string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{DataverseOptions.SectionName}:{propertyName} must be configured when Dataverse is enabled.");
        }
    }
}