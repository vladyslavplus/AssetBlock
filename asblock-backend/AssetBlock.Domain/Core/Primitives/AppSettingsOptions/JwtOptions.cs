namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class JwtOptions
{
    public const string SECTION_NAME = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
    /// <summary>Audience accepted only by the SignalR hub bearer scheme. Must differ from <see cref="Audience"/>.</summary>
    public string HubAudience { get; set; } = string.Empty;
    /// <summary>Hub token lifetime in seconds. Accepted range: 60–120.</summary>
    public int HubTokenSeconds { get; set; } = 90;
}
