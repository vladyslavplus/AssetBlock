namespace AssetBlock.Domain.Core.Dto;

public abstract record AssetProcessingPayload;
public abstract record AssetProcessingResult;

public sealed record ArchiveInspectionPayload : AssetProcessingPayload;
public sealed record ArchiveInspectionResult(int FileCount, long TotalSizeUncompressed) : AssetProcessingResult;

public sealed record MalwareScanPayload(string PolicyVersion) : AssetProcessingPayload;
public sealed record MalwareScanResult(bool IsClean) : AssetProcessingResult;

public sealed record ListingCopilotPayload(string PolicyVersion) : AssetProcessingPayload;
public sealed record ListingCopilotResult(bool Success, string ContentHash) : AssetProcessingResult;

public sealed record EmbeddingGenerationPayload(
    Guid AssetId,
    Guid AssetVersionId,
    long TargetRevision,
    string ContentHash,
    string ModelKey,
    string ContentSchemaVersion) : AssetProcessingPayload;

public sealed record EmbeddingGenerationResult(
    Guid AssetId,
    long SourceRevision,
    string ContentHash,
    string ModelKey,
    int Dimension,
    bool Success) : AssetProcessingResult;
