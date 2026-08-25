using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        var errors = new List<string>();
        if (!AiConfigurationRules.IsPolicyVersion(options.PromptPolicyVersion))
        {
            errors.Add("Ai:PromptPolicyVersion must be a bounded kebab-case version.");
        }

        if (!options.Enabled)
        {
            return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
        }

        if (!string.Equals(options.PromptPolicyVersion, AiPromptPolicies.LISTING_COPILOT_V1, StringComparison.Ordinal))
        {
            errors.Add("Ai:PromptPolicyVersion must match the implemented listing copilot prompt policy.");
        }

        if (!AiProviderParser.TryParse(options.Provider, out _))
        {
            errors.Add("Ai:Provider must be OpenRouter or Ollama when Ai:Enabled is true.");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
