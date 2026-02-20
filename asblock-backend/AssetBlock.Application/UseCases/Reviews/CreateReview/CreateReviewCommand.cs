using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Reviews.CreateReview;

public sealed record CreateReviewCommand(
    Guid AssetId,
    Guid UserId,
    int Rating,
    string? Comment) : IRequest<Result>;
