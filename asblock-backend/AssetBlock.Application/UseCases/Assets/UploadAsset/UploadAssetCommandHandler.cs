using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Application.UseCases.Assets.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.UploadAsset;

internal sealed class UploadAssetCommandHandler(
    ICategoryStore categoryStore,
    IAssetStore assetStore,
    ITagStore tagStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    ICacheService cache,
    IPublisher publisher,
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

        List<Tag>? existingTags = null;
        if (request.Request.Tags is { Count: > 0 })
        {
            var inputTags = request.Request.Tags.Select(t => t.Trim().ToLowerInvariant()).Distinct().ToList();
            existingTags = await tagStore.GetTagsByNames(inputTags, cancellationToken);
            if (existingTags.Count != inputTags.Count)
            {
                logger.LogWarning("Upload failed: one or more tags were not found in the database. Requested: {RequestedTags}, Found: {FoundTags}",
                    string.Join(", ", inputTags), string.Join(", ", existingTags.Select(t => t.Name)));
                return ResultError.Error<Guid>(ErrorCodes.ERR_TAG_NOT_FOUND);
            }
        }

        var assetId = Guid.NewGuid();
        var extension = Path.GetExtension(request.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        var storageKey = $"assets/{request.AuthorId}/{assetId}{extension}";

        using var cipherStream = new MemoryStream();
        try
        {
            await encryptionService.Encrypt(request.FileContent, cipherStream, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Encryption failed for asset {AssetId}", assetId);
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

        var now = DateTimeOffset.UtcNow;
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
            DownloadLimitPerHour = request.Request.DownloadLimitPerHour,
            CreatedAt = now
        };
        try
        {
            if (existingTags is { Count: > 0 })
            {
                await assetStore.AddWithTags(asset, existingTags, cancellationToken);
            }
            else
            {
                await assetStore.Add(asset, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DB add failed for asset {AssetId}; removing orphan from storage", assetId);
            try { await assetStorageService.Delete(storageKey, cancellationToken); }
            catch (Exception delEx) { logger.LogWarning(delEx, "Storage delete failed for {Key}", storageKey); }

            throw;
        }

        await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
        await publisher.Publish(new AssetCreatedEvent(assetId), cancellationToken);
        logger.LogInformation("Asset uploaded successfully {AssetId} by {AuthorId}", assetId, request.AuthorId);
        return Result.Success(assetId);
    }
}
