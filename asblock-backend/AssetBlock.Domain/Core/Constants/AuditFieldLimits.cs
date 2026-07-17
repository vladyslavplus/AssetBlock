namespace AssetBlock.Domain.Core.Constants;

/// <summary>Schema length limits for audit fields (shared by writer, HTTP accessor, validators).</summary>
public static class AuditFieldLimits
{
    public const int ACTION_MAX_LENGTH = 100;
    public const int RESOURCE_TYPE_MAX_LENGTH = 64;
    public const int RESOURCE_ID_MAX_LENGTH = 128;
    public const int TRACE_ID_MAX_LENGTH = 128;
    public const int IP_ADDRESS_MAX_LENGTH = 64;
    public const int USER_AGENT_MAX_LENGTH = 512;
    public const int MAX_METADATA_BYTES = 16 * 1024;
}
