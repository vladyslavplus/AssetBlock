namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class OllamaOptions
{
    public const string SECTION_NAME = "Ollama";

    public static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);
    public const int MIN_INPUT_CHARS = 1;
    public const int MAX_INPUT_CHARS = 50_000;
    public const int MIN_OUTPUT_TOKENS = 1;
    public const int MAX_OUTPUT_TOKENS = 8_000;
    public const int MAX_RESPONSE_BYTES = 64 * 1024;

    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = string.Empty;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
    public int MaxInputChars { get; set; } = 12_000;
    public int MaxOutputTokens { get; set; } = 1_000;
}
