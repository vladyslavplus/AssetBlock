using System.IO.Pipelines;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.UploadAsset;

internal sealed class UploadAssetCommandHandler(
    ICategoryStore categoryStore,
    IAssetStore assetStore,
    ITagStore tagStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    IAssetArchiveInspector archiveInspector,
    IOptions<FileUploadOptions> fileUploadOptions,
    IUnitOfWork unitOfWork,
    IOutboxStore outboxStore,
    ICacheService cache,
    ILogger<UploadAssetCommandHandler> logger) : IRequestHandler<UploadAssetCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(UploadAssetCommand request, CancellationToken cancellationToken)
    {
        var uploadOpts = fileUploadOptions.Value;

        if (request.FileLength <= 0)
        {
            return ResultError.Error<Guid>(ErrorCodes.ERR_FILE_REQUIRED);
        }

        if (request.FileLength > uploadOpts.MaxFileBytes)
        {
            return ResultError.Error<Guid>(ErrorCodes.ERR_FILE_TOO_LARGE);
        }

        var displayFileName = Path.GetFileName(request.FileName);
        if (string.IsNullOrWhiteSpace(displayFileName))
        {
            return ResultError.Error<Guid>(ErrorCodes.ERR_FILE_EXTENSION_NOT_ALLOWED);
        }

        if (!uploadOpts.TryMatchAllowedExtension(displayFileName, out var matchedExtension))
        {
            return ResultError.Error<Guid>(ErrorCodes.ERR_FILE_EXTENSION_NOT_ALLOWED);
        }

        var category = await categoryStore.GetById(request.Request.CategoryId, cancellationToken);
        if (category is null)
        {
            logger.LogDebug("Upload failed: category not found {CategoryId}", request.Request.CategoryId);
            return Result.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        }

        List<Tag>? existingTags = null;
        if (request.Request.Tags is { Count: > 0 })
        {
            var inputTags = request.Request.Tags.Select(t => t.Trim().ToLowerInvariant()).Distinct().ToList();
            existingTags = await tagStore.GetTagsByNames(inputTags, cancellationToken);
            if (existingTags.Count != inputTags.Count)
            {
                logger.LogWarning(
                    "Upload failed: one or more tags were not found in the database. Requested: {RequestedTags}, Found: {FoundTags}",
                    string.Join(", ", inputTags), string.Join(", ", existingTags.Select(t => t.Name)));
                return Result.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND);
            }
        }

        try
        {
            if (request.FileContent.CanSeek)
            {
                request.FileContent.Position = 0;
            }

            await archiveInspector.Inspect(request.FileContent, displayFileName, cancellationToken);

            if (request.FileContent.CanSeek)
            {
                request.FileContent.Position = 0;
            }
        }
        catch (ArchiveRejectedException ex)
        {
            logger.LogWarning(ex, "Archive rejected for upload by {AuthorId}", request.AuthorId);
            return ResultError.Error<Guid>(ErrorCodes.ERR_ARCHIVE_REJECTED);
        }

        var assetId = Guid.NewGuid();
        var storageKey = $"assets/{request.AuthorId}/{assetId}{matchedExtension}";
        var ciphertextLength = encryptionService.ComputeCiphertextLength(request.FileLength);

        try
        {
            await EncryptAndUpload(request.FileContent, storageKey, ciphertextLength, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Encrypt/upload failed for asset {AssetId}", assetId);
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
            FileName = displayFileName,
            DownloadLimitPerHour = request.Request.DownloadLimitPerHour,
            CreatedAt = now
        };
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                if (existingTags is { Count: > 0 })
                {
                    await assetStore.AddWithTags(asset, existingTags, ct);
                }
                else
                {
                    await assetStore.Add(asset, ct);
                }

                await outboxStore.Enqueue(
                    OutboxMessageTypes.ASSET_INDEX_UPSERT,
                    new AssetIndexUpsertPayload(assetId),
                    ct);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DB add failed for asset {AssetId}; removing orphan from storage", assetId);
            try
            {
                await assetStorageService.Delete(storageKey, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception delEx)
            {
                logger.LogWarning(delEx, "Storage delete failed for {Key}", storageKey);
            }

            throw;
        }

        try
        {
            await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache invalidation failed after upload {AssetId}", assetId);
        }

        logger.LogInformation("Asset uploaded successfully {AssetId} by {AuthorId}", assetId, request.AuthorId);
        return Result.Success(assetId);
    }

    private async Task EncryptAndUpload(
        Stream plain,
        string storageKey,
        long ciphertextLength,
        CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        Exception? encryptError = null;
        Exception? uploadError = null;

        var encryptTask = Task.Run(async () =>
        {
            try
            {
                await using var writerStream = pipe.Writer.AsStream(leaveOpen: true);
                await encryptionService.Encrypt(plain, writerStream, cancellationToken).ConfigureAwait(false);
                await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                encryptError = ex;
                await pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        var uploadTask = Task.Run(async () =>
        {
            try
            {
                await using var readerStream = pipe.Reader.AsStream(leaveOpen: true);
                await assetStorageService.Upload(storageKey, readerStream, ciphertextLength, cancellationToken)
                    .ConfigureAwait(false);
                await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                uploadError = ex;
                await pipe.Reader.CompleteAsync(ex).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        await Task.WhenAll(encryptTask, uploadTask).ConfigureAwait(false);

        if (encryptError is not null)
        {
            throw encryptError;
        }

        if (uploadError is not null)
        {
            throw uploadError;
        }
    }
}
