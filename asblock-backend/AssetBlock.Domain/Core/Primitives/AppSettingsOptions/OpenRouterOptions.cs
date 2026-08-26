namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class OpenRouterOptions
{
    private const string SECTION_NAME = "OpenRouter";
    public const string CONFIGURATION_PATH = AiOptions.SECTION_NAME + ":" + SECTION_NAME;

    public static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxRetryAfterBound = TimeSpan.FromHours(24);
    public const int MIN_INPUT_CHARS = 1;
    public const int MAX_INPUT_CHARS = 50_000;
    public const int MIN_OUTPUT_TOKENS = 1;
    public const int MAX_OUTPUT_TOKENS = 8_000;
    public const int MIN_API_KEY_LENGTH = 1;
    public const int MAX_API_KEY_LENGTH = 512;
    public const int MAX_RESPONSE_BYTES = 64 * 1024;

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ApiKey { get; set; } = string.Empty;
    public List<string> Models { get; set; } = [];
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(1);
    public int MaxInputChars { get; set; } = 12_000;
    public int MaxOutputTokens { get; set; } = 1_000;
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromHours(1);
    public string SiteUrl { get; set; } = string.Empty;
    public string AppName { get; set; } = "AssetBlock";

    /// <summary>
    /// When true, OpenRouter provider routing sets zdr=true. Stronger than data_collection deny and may reduce available endpoints.
    /// </summary>
    public bool ZeroDataRetention { get; set; }
}
