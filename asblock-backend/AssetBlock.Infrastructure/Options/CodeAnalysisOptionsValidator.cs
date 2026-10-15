using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class CodeAnalysisOptionsValidator : IValidateOptions<CodeAnalysisOptions>
{
    public ValidateOptionsResult Validate(string? name, CodeAnalysisOptions value)
    {
        List<string> failures = [];

        if (value.Capacity is < CodeAnalysisOptions.MIN_CAPACITY or > CodeAnalysisOptions.MAX_CAPACITY)
        {
            failures.Add($"{CodeAnalysisOptions.SECTION_NAME}:Capacity must be between {CodeAnalysisOptions.MIN_CAPACITY} and {CodeAnalysisOptions.MAX_CAPACITY}.");
        }

        if (value.JobDefinitionVersion is < CodeAnalysisOptions.MIN_JOB_DEFINITION_VERSION
            or > CodeAnalysisOptions.MAX_JOB_DEFINITION_VERSION)
        {
            failures.Add($"{CodeAnalysisOptions.SECTION_NAME}:JobDefinitionVersion must be between {CodeAnalysisOptions.MIN_JOB_DEFINITION_VERSION} and {CodeAnalysisOptions.MAX_JOB_DEFINITION_VERSION}.");
        }

        if (string.IsNullOrWhiteSpace(value.ServiceKey) || value.ServiceKey.Length > 128)
        {
            failures.Add($"{CodeAnalysisOptions.SECTION_NAME}:ServiceKey must be a short stable identifier.");
        }

        if (value.OperationTimeout <= TimeSpan.Zero
            || value.ServiceDeadline <= TimeSpan.Zero
            || value.ReservationHardDeadline <= TimeSpan.Zero
            || value.ReservationCleanupGrace < TimeSpan.Zero)
        {
            failures.Add($"{CodeAnalysisOptions.SECTION_NAME}: operation, service and reservation deadlines must be positive.");
        }

        // The service enforces the hard deadline, so the worker budget must never be the binding one.
        if (value.OperationTimeout <= value.ReservationHardDeadline)
        {
            failures.Add($"{CodeAnalysisOptions.SECTION_NAME}:OperationTimeout must exceed the reservation hard deadline.");
        }

        if (value.Enabled)
        {
            if (string.IsNullOrWhiteSpace(value.BaseUrl))
            {
                failures.Add($"{CodeAnalysisOptions.SECTION_NAME}:BaseUrl is required when analysis is enabled.");
            }

            if (string.IsNullOrWhiteSpace(value.ServiceSecret))
            {
                failures.Add($"{CodeAnalysisOptions.SECTION_NAME}:ServiceSecret is required when analysis is enabled.");
            }

            if (string.IsNullOrWhiteSpace(value.PolicyDigest) || value.PolicyDigest.Length != 64)
            {
                failures.Add($"{CodeAnalysisOptions.SECTION_NAME}:PolicyDigest must pin the exact 64-character policy digest.");
            }

            if (value.MaxResponseBytes <= 0 || value.MaxMetadataBytes <= 0)
            {
                failures.Add($"{CodeAnalysisOptions.SECTION_NAME}: response and metadata size limits must be positive.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
