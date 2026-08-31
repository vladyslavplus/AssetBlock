using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.PublishAssetVersion;

public sealed record PublishAssetVersionCommand(
    Guid AssetId,
    Guid AuthorId,
    PublishAssetVersionRequest Request,
    Stream FileContent,
    string FileName,
    long FileLength) : IRequest<Result<Guid>>;
