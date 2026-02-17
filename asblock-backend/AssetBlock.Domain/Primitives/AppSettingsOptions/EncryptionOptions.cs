namespace AssetBlock.Domain.Primitives.AppSettingsOptions;

/// <summary>
/// AES-GCM key (256-bit). KeyBase64 must be 44 chars (32 bytes base64).
/// </summary>
public sealed class EncryptionOptions
{
    public const string SECTION_NAME = "Encryption";

    public string KeyBase64 { get; set; } = string.Empty;
}
