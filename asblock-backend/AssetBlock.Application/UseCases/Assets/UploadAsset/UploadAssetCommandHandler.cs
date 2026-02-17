using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Entities;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.UploadAsset;

internal sealed class UploadAssetCommandHandler(
    ICategoryStore categoryStore,
    IAssetStore assetStore,
    IEncryptionService encryptionService,
    IAssetStorageService assetStorageService,
    ILogger<UploadAssetCommandHandler> logger) : IRequestHandler<UploadAssetCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(UploadAssetCommand request, CancellationToken cancellationToken)
    {
        var category = await categoryStore.GetById(request.Request.CategoryId, cancellationToken);
        if (category is null)
        {
            logger.LogDebug("Upload failed: category not found {CategoryId}", request.Request.CategoryId);
            return ResultError.Error<Guid>(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        }

        var assetId = Guid.NewGuid();
        var extension = Path.GetExtension(request.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }
        var storageKey = $"assets/{request.AuthorId}/{assetId}{extension}";

        using var cipherStream = new MemoryStream();
        byte[] nonce;
        try
        {
            nonce = await encryptionService.Encrypt(request.FileContent, cipherStream, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Encryption failed for asset upload");
            return ResultError.Error<Guid>(ErrorCodes.ERR_ASSET_UPLOAD_FAILED);
        }

        cipherStream.Position = 0;
        try
        {
            await assetStorageService.Upload(storageKey, cipherStream, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Storage upload failed for asset {AssetId}", assetId);
            return ResultError.Error<Guid>(ErrorCodes.ERR_ASSET_UPLOAD_FAILED);
        }

        var now = DateTime.UtcNow;
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = request.AuthorId,
            CategoryId = request.Request.CategoryId,
            Title = request.Request.Title,
            Description = request.Request.Description,
            Price = request.Request.Price,
            StorageKey = storageKey,
            FileName = request.FileName,
            EncryptionNonceBase64 = Convert.ToBase64String(nonce),
            CreatedAt = now,
            UpdatedAt = now
        };
        await assetStore.Add(asset, cancellationToken);
        logger.LogInformation("Asset uploaded successfully {AssetId} by {AuthorId}", assetId, request.AuthorId);
        return Result.Success(assetId);
    }
}
