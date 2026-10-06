namespace AssetBlock.Domain.Core.Dto.Moderation;

public enum IdempotentMutationStatus
{
    COMMITTED,
    REPLAYED,
    CONFLICT
}

public sealed record IdempotentMutationResult(IdempotentMutationStatus Status, string? ResultJson);
