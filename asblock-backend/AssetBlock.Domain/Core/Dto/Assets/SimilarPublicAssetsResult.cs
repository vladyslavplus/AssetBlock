namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>
/// Public similar-asset ranking result. UsedSemanticRefinement is true only when cosine rerank ran
/// for the whole shortlist; otherwise metadata ordering was used.
/// UsedPersonalization is true only when the authenticated opted-in account's affinity
/// actually reranked the shortlist; otherwise Phase A ordering was used.
/// Evidence carries per-candidate ranking inputs for explanations; null means unavailable
/// and maps to the same-category fallback for every candidate.
/// </summary>
public sealed record SimilarPublicAssetsResult(
    IReadOnlyList<AssetListItem> Items,
    bool UsedSemanticRefinement,
    bool UsedPersonalization = false,
    IReadOnlyDictionary<Guid, SimilarCandidateEvidence>? Evidence = null);
