using AssetBlock.Domain.Dto.Assets;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.UploadAsset;

public sealed record UploadAssetCommand(
    Guid AuthorId,
    UploadAssetRequest Request,
    Stream FileContent,
    string FileName) : IRequest<Result<Guid>>;
