using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Moderation;

/// <summary>FILE-level retrieval evidence. Own excerpt spans are not a verified matching pair.</summary>
public sealed record RetrievalCandidateEvidence(
    string QueryFileId,
    string QueryUnit,
    string Channels,
    int? TokenRank,
    double? TokenScore,
    int? SemanticRank,
    double? SemanticScore,
    bool Material);

public sealed record RetrievalQuerySummary(
    string QueryFileId,
    int TokenCandidates,
    int TokenRetained,
    int TokenOverflow,
    int SemanticCandidates,
    int SemanticRetained,
    int SemanticOverflow,
    int UnionRetained,
    int ReportOmitted,
    int ReportOmittedToken,
    int ReportOmittedSemantic,
    double? TokenMaxScore);

public sealed record RetrievalReportSummary(
    int TokenTop,
    int SemanticTop,
    int UnionBudget,
    IReadOnlyList<RetrievalQuerySummary> Queries);

public sealed record ModeratorRetrievalCandidate(
    Guid FindingId,
    string FindingKey,
    string FindingType,
    string OwnScopePath,
    int? OwnStartByte,
    int? OwnEndByte,
    int? OwnStartLine,
    int? OwnEndLine,
    string? ReferenceItemId,
    string? ReferenceOrigin,
    string ReferenceSensitivity,
    string ScoresAvailability,
    RetrievalCandidateEvidence? Scores,
    bool ResponseRequired,
    bool Answered,
    IReadOnlyList<ModeratorSellerResponse> Responses);

public sealed record ModeratorSellerResponse(SellerResponseType Kind, string Text, DateTimeOffset CreatedAt);

public sealed record ModeratorReportEvidence(
    Guid ReportId,
    string ScoresAvailability,
    string? ReferenceSnapshotId,
    string? ReferenceRevision,
    string? CoverageLevel,
    int? OmissionCount,
    int? FindingOverflow,
    RetrievalReportSummary? Retrieval,
    IReadOnlyList<ModeratorRetrievalCandidate> Candidates,
    bool ResponseRequirementsAvailable);

/// <summary>Admission-time reference and overflow identity, not a reconstruction from findings.</summary>
public sealed record PersistedRetrievalSummary(
    string ReferenceSnapshotId,
    string ReferenceRevision,
    int FindingOverflow,
    RetrievalReportSummary Retrieval);
