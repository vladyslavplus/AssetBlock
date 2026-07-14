using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class EncryptionOptionsValidator : IValidateOptions<EncryptionOptions>
{
    internal const int AES_256_KEY_LENGTH_BYTES = 32;

    public ValidateOptionsResult Validate(string? name, EncryptionOptions options)
    {
        if (OptionsValidation.IsMissingOrPlaceholder(options.KeyBase64))
        {
            return ValidateOptionsResult.Fail("Encryption:KeyBase64 must be non-empty.");
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(options.KeyBase64.Trim());
        }
        catch (FormatException)
        {
            return ValidateOptionsResult.Fail("Encryption:KeyBase64 must be valid Base64.");
        }

        if (keyBytes.Length != AES_256_KEY_LENGTH_BYTES)
        {
            return ValidateOptionsResult.Fail(
                $"Encryption:KeyBase64 must decode to exactly {AES_256_KEY_LENGTH_BYTES} bytes for AES-256.");
        }

        return ValidateOptionsResult.Success;
    }
}
