using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Reviews;

namespace AssetBlock.Application.UseCases.Reviews.GetReviews;

public sealed record GetReviewsQuery(Guid AssetId, GetReviewsRequest Request) : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<ReviewListItem>>>;
