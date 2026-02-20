using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Reviews;
using MediatR;

namespace AssetBlock.Application.UseCases.Reviews.GetReviewById;

public sealed record GetReviewByIdQuery(Guid Id) : IRequest<Result<ReviewDetailItem>>;
