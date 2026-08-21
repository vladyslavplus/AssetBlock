using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Domain.Core.Primitives.Storage;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Provider))
        {
            return ValidateOptionsResult.Fail(
                "Storage:Provider is required. Supported values: SeaweedFs, Minio.");
        }

        if (!StorageProvider.TryParse(options.Provider, out _))
        {
            return ValidateOptionsResult.Fail(
                $"Storage:Provider '{options.Provider}' is unknown. Supported values: SeaweedFs, Minio.");
        }

        return ValidateOptionsResult.Success;
    }
}
