using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class EncryptionOptionsValidator : IValidateOptions<EncryptionOptions>
{
    internal const int AES_256_KEY_LENGTH_BYTES = 32;

    public ValidateOptionsResult Validate(string? name, EncryptionOptions options)
    {
        if (options.Keys is { Count: > 0 })
        {
            if (string.IsNullOrWhiteSpace(options.CurrentKeyId))
            {
                return ValidateOptionsResult.Fail("Encryption:CurrentKeyId must be specified when Encryption:Keys is configured.");
            }

            if (!options.Keys.ContainsKey(options.CurrentKeyId))
            {
                return ValidateOptionsResult.Fail($"Encryption:CurrentKeyId '{options.CurrentKeyId}' was not found in Encryption:Keys.");
            }

            if (!string.IsNullOrWhiteSpace(options.LegacyKeyId) && !options.Keys.ContainsKey(options.LegacyKeyId))
            {
                return ValidateOptionsResult.Fail($"Encryption:LegacyKeyId '{options.LegacyKeyId}' was not found in Encryption:Keys.");
            }

            foreach (var (keyId, keyBase64) in options.Keys)
            {
                if (string.IsNullOrWhiteSpace(keyId))
                {
                    return ValidateOptionsResult.Fail("Encryption:Keys contains an empty key identifier.");
                }

                if (OptionsValidation.IsMissingOrPlaceholder(keyBase64))
                {
                    return ValidateOptionsResult.Fail($"Encryption:Keys['{keyId}'] must be non-empty.");
                }

                var error = ValidateKeyBase64(keyBase64, $"Encryption:Keys['{keyId}']");
                if (error is not null)
                {
                    return ValidateOptionsResult.Fail(error);
                }
            }

            return ValidateOptionsResult.Success;
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.KeyBase64))
        {
            return ValidateOptionsResult.Fail("Encryption:KeyBase64 must be non-empty.");
        }

        var keyError = ValidateKeyBase64(options.KeyBase64, "Encryption:KeyBase64");
        return keyError is not null ? ValidateOptionsResult.Fail(keyError) : ValidateOptionsResult.Success;
    }

    private static string? ValidateKeyBase64(string keyBase64, string fieldName)
    {
        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(keyBase64.Trim());
        }
        catch (FormatException)
        {
            return $"{fieldName} must be valid Base64.";
        }

        if (keyBytes.Length != AES_256_KEY_LENGTH_BYTES)
        {
            return $"{fieldName} must decode to exactly {AES_256_KEY_LENGTH_BYTES} bytes for AES-256.";
        }

        return null;
    }
}
