using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class EncryptionOptionsValidator : IValidateOptions<EncryptionOptions>
{
    internal const int AES_256_KEY_LENGTH_BYTES = 32;
    private const int MAX_KEY_ID_BYTES = 64;

    public ValidateOptionsResult Validate(string? name, EncryptionOptions options)
    {
        if (options.Keys.Count == 0)
        {
            return ValidateOptionsResult.Fail("Encryption:Keys must contain at least one configured encryption key.");
        }

        if (string.IsNullOrWhiteSpace(options.CurrentKeyId))
        {
            return ValidateOptionsResult.Fail("Encryption:CurrentKeyId must be specified.");
        }

        if (!options.Keys.ContainsKey(options.CurrentKeyId))
        {
            return ValidateOptionsResult.Fail($"Encryption:CurrentKeyId '{options.CurrentKeyId}' was not found in Encryption:Keys.");
        }

        foreach (var (keyId, keyBase64) in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(keyId))
            {
                return ValidateOptionsResult.Fail("Encryption:Keys contains an empty key identifier.");
            }

            if (System.Text.Encoding.UTF8.GetByteCount(keyId) > MAX_KEY_ID_BYTES)
            {
                return ValidateOptionsResult.Fail($"Encryption:Keys['{keyId}'] key identifier exceeds maximum length of {MAX_KEY_ID_BYTES} bytes.");
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
