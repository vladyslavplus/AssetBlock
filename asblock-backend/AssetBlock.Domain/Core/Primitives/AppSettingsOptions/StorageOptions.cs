namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class StorageOptions
{
    public const string SECTION_NAME = "Storage";

    /// <summary>Active object-storage provider: SeaweedFs or Minio (case-insensitive).</summary>
    public string Provider { get; set; } = string.Empty;
}
