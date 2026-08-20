namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Named ASP.NET Core authorization policy names.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Requires an authenticated user whose current EmailVerifiedAt is set (DB lookup, not a JWT claim).
    /// </summary>
    public const string VERIFIED_EMAIL = "VERIFIED_EMAIL";
}
