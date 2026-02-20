namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// JWT claim type names (RFC 7519). Use these instead of hardcoded strings when building tokens.
/// In ASP.NET Core JWT Bearer, "sub" is mapped to ClaimTypes.NameIdentifier when reading User.Claims.
/// </summary>
public static class JwtClaimTypes
{
    public const string SUB = "sub";
    public const string JTI = "jti";
    public const string EMAIL = "email";
    public const string ROLE = "role";
}
