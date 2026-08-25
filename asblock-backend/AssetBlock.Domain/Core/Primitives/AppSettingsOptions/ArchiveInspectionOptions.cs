namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class ArchiveInspectionOptions
{
    public const string SECTION_NAME = "ArchiveInspection";

    public const int MAX_ENTRIES_UPPER = 50_000;
    public const long MAX_TOTAL_EXPANDED_BYTES_UPPER = 8L * 1024 * 1024 * 1024;
    public const int MAX_PATH_LENGTH_UPPER = 2048;
    public const int MAX_PATH_DEPTH_UPPER = 64;
    public const double MAX_COMPRESSION_RATIO_UPPER = 10_000d;
    public const int MAX_MANIFEST_FILES_UPPER = 64;

    public int MaxEntries { get; set; } = 10_000;
    public long MaxTotalExpandedBytes { get; set; } = 1L * 1024 * 1024 * 1024; // 1 GiB
    public long MaxEntryExpandedBytes { get; set; } = 500L * 1024 * 1024; // 500 MiB
    public double MaxCompressionRatio { get; set; } = 100.0;
    public int MaxPathLength { get; set; } = 1024;
    /// <summary>
    /// Maximum filesystem path segment depth inside the outer archive.
    /// This is not archive-nesting depth; nested archives are left to ClamAV.
    /// </summary>
    public int MaxPathDepth { get; set; } = 32;
    public int MaxReadmeBytes { get; set; } = 16384;
    public int MaxManifestFiles { get; set; } = 8;
    public int MaxManifestBytes { get; set; } = 16384;
}
