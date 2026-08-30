namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

/// <summary>
/// AES-GCM keyring configuration (256-bit keys). Each key in Keys must be 32 bytes base64.
/// </summary>
public sealed class EncryptionOptions
{
    public const string SECTION_NAME = "Encryption";

    public string CurrentKeyId { get; set; } = string.Empty;
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
