using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Reviews.DeleteReview;

public sealed record DeleteReviewCommand(Guid Id) : IRequest<Result>;
