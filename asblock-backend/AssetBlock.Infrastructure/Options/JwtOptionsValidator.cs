using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    internal const int MIN_SIGNING_KEY_LENGTH = 32;

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();

        if (OptionsValidation.IsMissingOrPlaceholder(options.Issuer))
        {
            failures.Add("Jwt:Issuer must be non-empty.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.Audience))
        {
            failures.Add("Jwt:Audience must be non-empty.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.Key))
        {
            failures.Add("Jwt:Key must be non-empty.");
        }
        else if (options.Key.Trim().Length < MIN_SIGNING_KEY_LENGTH)
        {
            failures.Add($"Jwt:Key must be at least {MIN_SIGNING_KEY_LENGTH} characters.");
        }

        if (options.AccessTokenMinutes <= 0)
        {
            failures.Add("Jwt:AccessTokenMinutes must be a positive integer.");
        }

        if (options.RefreshTokenDays <= 0)
        {
            failures.Add("Jwt:RefreshTokenDays must be a positive integer.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
