using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Assets;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.UploadAsset;

public sealed record UploadAssetCommand(
    Guid AuthorId,
    UploadAssetRequest Request,
    Stream FileContent,
    string FileName) : IRequest<Result<Guid>>;
