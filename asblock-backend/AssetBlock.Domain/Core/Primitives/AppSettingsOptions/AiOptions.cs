using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class AiOptions
{
    public const string SECTION_NAME = "Ai";

    public bool Enabled { get; set; }
    public string Provider { get; set; } = "OpenRouter";
    public string PromptPolicyVersion { get; set; } = AiPromptPolicies.LISTING_COPILOT_V1;
}
