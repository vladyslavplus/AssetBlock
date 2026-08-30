namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

/// <summary>
/// AES-GCM key (256-bit). KeyBase64 must be 44 chars (32 bytes base64).
/// </summary>
public sealed class EncryptionOptions
{
    public const string SECTION_NAME = "Encryption";
    public const string DEFAULT_KEY_ID = "k1";

    public string KeyBase64 { get; set; } = string.Empty;
    public string CurrentKeyId { get; set; } = DEFAULT_KEY_ID;
    public string? LegacyKeyId { get; set; }
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
