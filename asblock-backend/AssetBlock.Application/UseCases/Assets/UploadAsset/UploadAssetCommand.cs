using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.UploadAsset;

public sealed record UploadAssetCommand(
    Guid AuthorId,
    UploadAssetRequest Request,
    Stream FileContent,
    string FileName,
    long FileLength) : IRequest<Result<Guid>>;
