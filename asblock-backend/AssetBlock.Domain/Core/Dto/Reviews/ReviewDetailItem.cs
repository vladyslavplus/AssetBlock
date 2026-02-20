namespace AssetBlock.Domain.Core.Dto.Reviews;

public sealed record ReviewDetailItem(
    Guid Id,
    Guid AssetId,
    Guid UserId,
    string? Username,
    int Rating,
    string? Comment,
    DateTimeOffset CreatedAt);
