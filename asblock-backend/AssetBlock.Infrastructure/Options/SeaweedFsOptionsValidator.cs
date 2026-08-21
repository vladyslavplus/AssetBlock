using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Domain.Core.Primitives.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class SeaweedFsOptionsValidator(IConfiguration configuration) : IValidateOptions<SeaweedFsOptions>
{
    public ValidateOptionsResult Validate(string? name, SeaweedFsOptions options)
    {
        if (!IsActiveProvider())
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (!OptionsValidation.TryValidateS3CompatibleEndpoint(
                options.Endpoint,
                options.UseSsl,
                SeaweedFsOptions.SECTION_NAME,
                out var endpointError))
        {
            failures.Add(endpointError ?? "SeaweedFs:Endpoint is invalid.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.Bucket))
        {
            failures.Add("SeaweedFs:Bucket must be non-empty.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.AccessKey))
        {
            failures.Add("SeaweedFs:AccessKey must be non-empty.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.SecretKey))
        {
            failures.Add("SeaweedFs:SecretKey must be non-empty.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private bool IsActiveProvider()
    {
        var provider = configuration.GetSection(StorageOptions.SECTION_NAME)["Provider"];
        return StorageProvider.TryParse(provider, out var canonical)
            && canonical == StorageProvider.SEAWEED_FS;
    }
}
