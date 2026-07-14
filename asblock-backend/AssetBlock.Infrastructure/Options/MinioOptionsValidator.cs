using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class MinioOptionsValidator : IValidateOptions<MinioOptions>
{
    public ValidateOptionsResult Validate(string? name, MinioOptions options)
    {
        var failures = new List<string>();

        if (!OptionsValidation.TryValidateMinioEndpoint(options.Endpoint, options.UseSsl, out var endpointError))
        {
            failures.Add(endpointError ?? "Minio:Endpoint is invalid.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.Bucket))
        {
            failures.Add("Minio:Bucket must be non-empty.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.AccessKey))
        {
            failures.Add("Minio:AccessKey must be non-empty.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.SecretKey))
        {
            failures.Add("Minio:SecretKey must be non-empty.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
