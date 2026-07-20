using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class DataProtectionOptionsValidator : IValidateOptions<DataProtectionOptions>
{
    public ValidateOptionsResult Validate(string? name, DataProtectionOptions options)
    {
        if (OptionsValidation.IsMissingOrPlaceholder(options.KeysPath))
        {
            return ValidateOptionsResult.Fail("DataProtection:KeysPath must be a non-empty writable directory path.");
        }

        var mode = options.ProtectionMode.Trim();
        if (mode.Length > 0
            && !mode.Equals(DataProtectionOptions.MODE_DPAPI, StringComparison.OrdinalIgnoreCase)
            && !mode.Equals(DataProtectionOptions.MODE_CERTIFICATE, StringComparison.OrdinalIgnoreCase)
            && !mode.Equals(DataProtectionOptions.MODE_KMS, StringComparison.OrdinalIgnoreCase)
            && !mode.Equals(DataProtectionOptions.MODE_NONE, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "DataProtection:ProtectionMode must be Dpapi, Certificate, Kms, None, or empty (auto).");
        }

        if (mode.Equals(DataProtectionOptions.MODE_CERTIFICATE, StringComparison.OrdinalIgnoreCase))
        {
            var hasPath = !OptionsValidation.IsMissingOrPlaceholder(options.CertificatePath);
            var hasThumb = !OptionsValidation.IsMissingOrPlaceholder(options.CertificateThumbprint);
            if (!hasPath && !hasThumb)
            {
                return ValidateOptionsResult.Fail(
                    "DataProtection:CertificatePath or DataProtection:CertificateThumbprint is required when ProtectionMode is Certificate.");
            }
        }

        if (mode.Equals(DataProtectionOptions.MODE_KMS, StringComparison.OrdinalIgnoreCase)
            && OptionsValidation.IsMissingOrPlaceholder(options.KmsKeyId))
        {
            return ValidateOptionsResult.Fail(
                "DataProtection:KmsKeyId is required when ProtectionMode is Kms.");
        }

        return ValidateOptionsResult.Success;
    }
}
