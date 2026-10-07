using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetAssetDeclaration;

internal sealed class GetAssetDeclarationQueryHandler(IModerationFoundationStore moderationFoundationStore)
    : IRequestHandler<GetAssetDeclarationQuery, Result<SellerDeclarationSnapshotDto>>
{
    public async Task<Result<SellerDeclarationSnapshotDto>> Handle(GetAssetDeclarationQuery request, CancellationToken cancellationToken)
    {
        // A present workspace without stored declaration content returns a snapshot with a null
        // Declaration; a missing or mismatched workspace (including a foreign version id) is 404.
        SellerDeclarationSnapshotDto? snapshot = await moderationFoundationStore.GetOwnerDeclarationSnapshot(
            request.AssetId,
            request.AssetVersionId,
            request.OwnerId,
            cancellationToken);

        if (snapshot is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_DRAFT_NOT_FOUND);
        }

        return Result.Success(snapshot);
    }
}
