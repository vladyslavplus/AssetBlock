using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetAssetDeclaration;

public sealed record GetAssetDeclarationQuery(Guid AssetId, Guid? AssetVersionId, Guid OwnerId)
    : IRequest<Result<SellerDeclarationSnapshotDto>>;
