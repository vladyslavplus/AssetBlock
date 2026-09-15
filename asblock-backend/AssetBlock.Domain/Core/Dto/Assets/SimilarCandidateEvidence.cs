namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>
/// Per-candidate evidence actually used by the ranking path. All flags describe
/// nonzero signals that participated in ordering; zero signals fall through to
/// weaker reasons. SharedTagCount is the exact source/candidate tag intersection.
/// </summary>
public sealed record SimilarCandidateEvidence(
    bool HasPersonalClick,
    bool HasPersonalTagScore,
    bool HasPopularitySignal,
    int SharedTagCount);
