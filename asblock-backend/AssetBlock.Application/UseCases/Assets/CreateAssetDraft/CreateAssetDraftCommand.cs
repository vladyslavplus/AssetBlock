using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.CreateAssetDraft;

public sealed record CreateAssetDraftCommand(
    Guid OwnerId,
    Guid OperationId,
    string Title,
    string? Description,
    decimal Price,
    Guid CategoryId,
    int? DownloadLimitPerHour) : IRequest<Result<SellerDraftCreatedDto>>;
