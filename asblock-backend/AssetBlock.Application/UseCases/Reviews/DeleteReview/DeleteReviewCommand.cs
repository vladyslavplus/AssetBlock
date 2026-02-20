using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Reviews.DeleteReview;

public sealed record DeleteReviewCommand(Guid Id) : IRequest<Result>;
