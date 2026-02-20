namespace AssetBlock.Domain.Core.Dto.Reviews;

public sealed record CreateReviewRequest(int Rating, string? Comment);
