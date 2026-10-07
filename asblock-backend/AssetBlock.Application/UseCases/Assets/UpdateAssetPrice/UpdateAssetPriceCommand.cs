using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Assets.UpdateAssetPrice;

/// <summary>Dedicated bounded price-only operation. Material metadata is not accepted here.</summary>
public sealed record UpdateAssetPriceCommand(
    Guid AssetId,
    Guid OwnerId,
    decimal Price) : IRequest<Result>;
