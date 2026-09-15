namespace AssetBlock.Domain.Core.Dto.Recommendations;

/// <summary>Per-request personal signals for one account, source, and candidate set.</summary>
public sealed record PersonalAffinitySignals(
    IReadOnlyDictionary<Guid, long> ClicksByTargetId,
    IReadOnlyDictionary<Guid, long> TagScoreByCandidateId);
