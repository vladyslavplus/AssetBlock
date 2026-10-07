using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetAssetDraft;

internal sealed class GetAssetDraftQueryHandler(IModerationFoundationStore moderationFoundationStore)
    : IRequestHandler<GetAssetDraftQuery, Result<SellerDraftSnapshotDto>>
{
    public async Task<Result<SellerDraftSnapshotDto>> Handle(GetAssetDraftQuery request, CancellationToken cancellationToken)
    {
        SellerDraftSnapshotDto? draft = await moderationFoundationStore.GetOwnerDraftSnapshot(
            request.AssetId,
            request.OwnerId,
            cancellationToken);

        if (draft is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        return Result.Success(draft);
    }
}
