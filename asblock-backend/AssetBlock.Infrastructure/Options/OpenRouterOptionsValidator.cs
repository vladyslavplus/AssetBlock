using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class OpenRouterOptionsValidator(IConfiguration configuration) : IValidateOptions<OpenRouterOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenRouterOptions options)
    {
        if (!AiConfigurationRules.IsActiveProvider(configuration, AiProviderKind.OPENROUTER))
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        if (!AiConfigurationRules.IsAbsoluteHttpOrHttps(options.BaseUrl, allowHttps: true, requireLoopback: false))
        {
            errors.Add("Ai:OpenRouter:BaseUrl must be an absolute HTTP or HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey)
            || options.ApiKey.Length < OpenRouterOptions.MIN_API_KEY_LENGTH
            || options.ApiKey.Length > OpenRouterOptions.MAX_API_KEY_LENGTH
            || options.ApiKey.Any(char.IsWhiteSpace))
        {
            errors.Add("Ai:OpenRouter:ApiKey is required when Ai:Enabled is true and Ai:Provider is OpenRouter.");
        }

        if (options.Models.Count is 0 or > ListingSuggestionBounds.MAX_OPENROUTER_MODELS)
        {
            errors.Add($"Ai:OpenRouter:Models must contain between 1 and {ListingSuggestionBounds.MAX_OPENROUTER_MODELS} entries.");
        }
        else if (options.Models.Any(model => !AiConfigurationRules.IsModelId(model)))
        {
            errors.Add("Ai:OpenRouter:Models must contain bounded, distinct model ids.");
        }
        else if (options.Models.Count != options.Models.Distinct(StringComparer.Ordinal).Count())
        {
            errors.Add("Ai:OpenRouter:Models must be ordered distinct model ids.");
        }

        if (options.Timeout < OpenRouterOptions.MinTimeout || options.Timeout > OpenRouterOptions.MaxTimeout)
        {
            errors.Add("Ai:OpenRouter:Timeout must be between 5 seconds and 5 minutes.");
        }

        if (options.MaxInputChars is < OpenRouterOptions.MIN_INPUT_CHARS or > OpenRouterOptions.MAX_INPUT_CHARS)
        {
            errors.Add($"Ai:OpenRouter:MaxInputChars must be between {OpenRouterOptions.MIN_INPUT_CHARS} and {OpenRouterOptions.MAX_INPUT_CHARS}.");
        }

        if (options.MaxOutputTokens is < OpenRouterOptions.MIN_OUTPUT_TOKENS or > OpenRouterOptions.MAX_OUTPUT_TOKENS)
        {
            errors.Add($"Ai:OpenRouter:MaxOutputTokens must be between {OpenRouterOptions.MIN_OUTPUT_TOKENS} and {OpenRouterOptions.MAX_OUTPUT_TOKENS}.");
        }

        if (options.MaxRetryAfter < OpenRouterOptions.MinRetryAfter || options.MaxRetryAfter > OpenRouterOptions.MaxRetryAfterBound)
        {
            errors.Add("Ai:OpenRouter:MaxRetryAfter must be between 1 second and 24 hours.");
        }

        if (!string.IsNullOrWhiteSpace(options.SiteUrl)
            && !AiConfigurationRules.IsAbsoluteHttpOrHttps(options.SiteUrl, allowHttps: true, requireLoopback: false))
        {
            errors.Add("Ai:OpenRouter:SiteUrl must be empty or an absolute HTTP or HTTPS URL.");
        }

        if (!AiConfigurationRules.IsAppName(options.AppName))
        {
            errors.Add("Ai:OpenRouter:AppName must be a bounded application name.");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
