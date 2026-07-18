namespace AssetBlock.Domain.Core.Constants;

/// <summary>Bounds for transactional email content (composer + outbox validation).</summary>
public static class EmailContentLimits
{
    public const int MAX_SUBJECT_LENGTH = 200;
    public const int MAX_BODY_LENGTH = 64 * 1024;
}
