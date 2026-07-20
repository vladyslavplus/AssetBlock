namespace AssetBlock.Domain.Core.Constants;

/// <summary>Email action expiry and resend cooldown durations.</summary>
public static class EmailActionConstants
{
    public static readonly TimeSpan VerificationExpiry = TimeSpan.FromHours(24);
    public static readonly TimeSpan PasswordResetExpiry = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan EmailChangeExpiry = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Max length of a protected action token accepted from clients (before decode).</summary>
    public const int MAX_PROTECTED_TOKEN_LENGTH = 4096;

    public const string DATA_PROTECTION_PURPOSE = "AssetBlock.EmailActions.v1";
}
