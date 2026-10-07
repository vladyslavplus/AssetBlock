using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetAssetDraft;

public sealed record GetAssetDraftQuery(Guid AssetId, Guid OwnerId)
    : IRequest<Result<SellerDraftSnapshotDto>>;
