namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class AnalyticsRateLimitingOptions
{
    public const string SECTION_NAME = "AnalyticsRateLimiting";

    public string BffSigningSecret { get; set; } = string.Empty;
}
