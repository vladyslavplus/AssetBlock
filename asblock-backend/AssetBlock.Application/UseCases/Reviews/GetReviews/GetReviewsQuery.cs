using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Reviews;
using MediatR;

namespace AssetBlock.Application.UseCases.Reviews.GetReviews;

public sealed record GetReviewsQuery(Guid AssetId, GetReviewsRequest Request) : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<ReviewListItem>>>;
