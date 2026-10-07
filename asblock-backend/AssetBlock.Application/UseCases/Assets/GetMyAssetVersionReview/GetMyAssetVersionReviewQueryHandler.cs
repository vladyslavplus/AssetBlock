using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetMyAssetVersionReview;

internal sealed class GetMyAssetVersionReviewQueryHandler(IModerationFoundationStore moderationFoundationStore)
    : IRequestHandler<GetMyAssetVersionReviewQuery, Result<SellerVersionReviewDto>>
{
    public async Task<Result<SellerVersionReviewDto>> Handle(GetMyAssetVersionReviewQuery request, CancellationToken cancellationToken)
    {
        SellerVersionReviewDto? review = await moderationFoundationStore.GetOwnerVersionReview(
            request.AssetVersionId,
            request.OwnerId,
            cancellationToken);

        if (review is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_VERSION_NOT_FOUND);
        }

        return Result.Success(review);
    }
}
