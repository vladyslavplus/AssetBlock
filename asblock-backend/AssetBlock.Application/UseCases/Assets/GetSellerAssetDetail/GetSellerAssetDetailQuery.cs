using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetSellerAssetDetail;

public sealed record GetSellerAssetDetailQuery(Guid AssetId, Guid OwnerUserId)
    : IRequest<Result<SellerAssetDetailItem>>;
