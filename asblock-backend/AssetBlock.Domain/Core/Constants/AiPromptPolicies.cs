namespace AssetBlock.Domain.Core.Constants;

public static class AiPromptPolicies
{
    public const string LISTING_COPILOT_V1 = "listing-copilot-v1";
    public const int POLICY_VERSION_MIN_LENGTH = 1;
    public const int POLICY_VERSION_MAX_LENGTH = 64;
    public const string MODEL_POLICY_SCHEMA_VERSION = "1";
    public const int MODEL_POLICY_SCHEMA_VERSION_NUMBER = 1;
    public const string DEFAULT_MODEL_POLICY_PATH = "ai/model-policy.json";
}
