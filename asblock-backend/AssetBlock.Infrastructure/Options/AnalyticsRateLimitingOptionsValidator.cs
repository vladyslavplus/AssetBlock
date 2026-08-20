using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class AnalyticsRateLimitingOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<AnalyticsRateLimitingOptions>
{
    private const int MIN_SIGNING_SECRET_LENGTH = 32;

    public ValidateOptionsResult Validate(string? name, AnalyticsRateLimitingOptions options)
    {
        var secret = options.BffSigningSecret.Trim();
        var secretMissing = OptionsValidation.IsMissingOrPlaceholder(secret);

        if (secretMissing)
        {
            // Local Development / IntegrationTesting may omit the secret (signed BFF transport
            // stays unavailable). Every other environment must configure it.
            if (environment.IsDevelopment() || environment.IsEnvironment("IntegrationTesting"))
            {
                return ValidateOptionsResult.Success;
            }

            return ValidateOptionsResult.Fail(
                "AnalyticsRateLimiting:BffSigningSecret must be non-empty outside Development/IntegrationTesting.");
        }

        // A provided secret must always pass placeholder/length checks, including Development.
        if (secret.Length < MIN_SIGNING_SECRET_LENGTH)
        {
            return ValidateOptionsResult.Fail(
                $"AnalyticsRateLimiting:BffSigningSecret must be at least {MIN_SIGNING_SECRET_LENGTH} characters.");
        }

        return ValidateOptionsResult.Success;
    }
}
