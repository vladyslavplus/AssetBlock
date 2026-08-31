namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Well-known values for the <c>token_use</c> JWT claim used to distinguish token purpose at validation time.
/// </summary>
public static class JwtClaimValues
{
    /// <summary>Hub-only token that the SignalR bearer scheme accepts and the REST API scheme rejects.</summary>
    public const string TOKEN_USE_SIGNALR = "signalr";
}
