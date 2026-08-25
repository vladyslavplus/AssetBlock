using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class OllamaOptionsValidator(
    IConfiguration configuration,
    IAiModelPolicyCatalog modelPolicyCatalog) : IValidateOptions<OllamaOptions>
{
    public ValidateOptionsResult Validate(string? name, OllamaOptions options)
    {
        if (!AiConfigurationRules.IsActiveProvider(configuration, AiProviderKind.OLLAMA))
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        if (!AiConfigurationRules.IsAbsoluteHttpOrHttps(options.BaseUrl, allowHttps: false, requireLoopback: true))
        {
            errors.Add("Ollama:BaseUrl must be an absolute loopback HTTP URL.");
        }

        if (!AiConfigurationRules.IsModelId(options.Model))
        {
            errors.Add("Ollama:Model is required when Ai:Enabled is true and Ai:Provider is Ollama.");
        }
        else if (!modelPolicyCatalog.TryGet(AiProviderKind.OLLAMA, options.Model, out var entry)
            || !entry.StructuredOutput
            || entry.UseCase != AiModelUseCase.LISTING_COPILOT
            || entry.Privacy != AiPrivacyDecision.LOCAL_ONLY
            || !AiConfigurationRules.IsSha256Digest(entry.Digest))
        {
            errors.Add("Ollama:Model must have an approved local listing-copilot policy record with an exact sha256 digest.");
        }
        else if (!AiConfigurationRules.ConfiguredLimitsFitPolicy(options.MaxInputChars, options.MaxOutputTokens, entry))
        {
            errors.Add("Ollama input and output limits must not exceed the approved policy limits for the configured model.");
        }

        if (options.Timeout < OllamaOptions.MinTimeout || options.Timeout > OllamaOptions.MaxTimeout)
        {
            errors.Add("Ollama:Timeout must be between 5 seconds and 10 minutes.");
        }

        if (options.MaxInputChars is < OllamaOptions.MIN_INPUT_CHARS or > OllamaOptions.MAX_INPUT_CHARS)
        {
            errors.Add($"Ollama:MaxInputChars must be between {OllamaOptions.MIN_INPUT_CHARS} and {OllamaOptions.MAX_INPUT_CHARS}.");
        }

        if (options.MaxOutputTokens is < OllamaOptions.MIN_OUTPUT_TOKENS or > OllamaOptions.MAX_OUTPUT_TOKENS)
        {
            errors.Add($"Ollama:MaxOutputTokens must be between {OllamaOptions.MIN_OUTPUT_TOKENS} and {OllamaOptions.MAX_OUTPUT_TOKENS}.");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
