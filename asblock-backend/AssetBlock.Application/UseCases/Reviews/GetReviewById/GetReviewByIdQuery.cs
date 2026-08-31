using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Reviews;

namespace AssetBlock.Application.UseCases.Reviews.GetReviewById;

public sealed record GetReviewByIdQuery(Guid Id) : IRequest<Result<ReviewDetailItem>>;
