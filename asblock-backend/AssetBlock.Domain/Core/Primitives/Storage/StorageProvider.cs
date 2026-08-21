namespace AssetBlock.Domain.Core.Primitives.Storage;

/// <summary>Canonical Storage:Provider values (comparison is case-insensitive).</summary>
public static class StorageProvider
{
    public const string SEAWEED_FS = "SeaweedFs";
    public const string MINIO = "Minio";

    public static bool TryParse(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(value.Trim(), SEAWEED_FS, StringComparison.OrdinalIgnoreCase))
        {
            canonical = SEAWEED_FS;
            return true;
        }

        if (string.Equals(value.Trim(), MINIO, StringComparison.OrdinalIgnoreCase))
        {
            canonical = MINIO;
            return true;
        }

        return false;
    }
}
