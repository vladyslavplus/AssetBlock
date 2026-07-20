using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Assets;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.PublishAssetVersion;

public sealed record PublishAssetVersionCommand(
    Guid AssetId,
    Guid AuthorId,
    PublishAssetVersionRequest Request,
    Stream FileContent,
    string FileName,
    long FileLength) : IRequest<Result<Guid>>;
