using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace AssetBlock.WebApi.Constants;

/// <summary>
/// Named JWT bearer scheme identifiers. Use when referencing a specific scheme in [Authorize] or policy builders.
/// </summary>
public static class JwtAuthenticationSchemes
{
    /// <summary>Default API bearer scheme — validates session access tokens. Rejects hub tokens (wrong audience + token_use).</summary>
    public const string API = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>Hub bearer scheme — validates hub-only tokens. Accepts the query-string access_token on the notifications hub path only.</summary>
    public const string HUB = "HubBearer";
}
