using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetMyAssetVersionReview;

public sealed record GetMyAssetVersionReviewQuery(Guid AssetVersionId, Guid OwnerId)
    : IRequest<Result<SellerVersionReviewDto>>;
