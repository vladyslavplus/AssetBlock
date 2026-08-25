namespace AssetBlock.Domain.Core.Dto;

public sealed record ArchiveSafetyResult(
    bool IsSafe,
    string? ErrorCode,
    string? ErrorSummary,
    int FileCount,
    long TotalExpandedBytes,
    string? ReadmeContent,
    ArchiveAnalysisManifestMetadata? ManifestMetadata)
{
    public static ArchiveSafetyResult Safe(
        int fileCount,
        long totalExpandedBytes,
        string? readmeContent,
        ArchiveAnalysisManifestMetadata? manifestMetadata) =>
        new(
            IsSafe: true,
            ErrorCode: null,
            ErrorSummary: null,
            FileCount: fileCount,
            TotalExpandedBytes: totalExpandedBytes,
            ReadmeContent: readmeContent,
            ManifestMetadata: manifestMetadata);

    public static ArchiveSafetyResult Rejected(string errorCode, string errorSummary, long totalExpandedBytes = 0) =>
        new(
            IsSafe: false,
            ErrorCode: errorCode,
            ErrorSummary: errorSummary,
            FileCount: 0,
            TotalExpandedBytes: totalExpandedBytes,
            ReadmeContent: null,
            ManifestMetadata: null);
}
