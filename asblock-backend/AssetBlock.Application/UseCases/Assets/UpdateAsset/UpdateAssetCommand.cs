using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.UpdateAsset;

public sealed record UpdateAssetCommand(
    Guid AssetId,
    Guid UserId,
    string? Title,
    string? Description,
    decimal? Price,
    Guid? CategoryId) : IRequest<Result>;
