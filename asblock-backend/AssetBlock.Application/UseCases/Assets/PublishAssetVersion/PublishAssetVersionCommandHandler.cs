using System.IO.Pipelines;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.PublishAssetVersion;

internal sealed class PublishAssetVersionCommandHandler(
    IAssetStore assetStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    IAssetArchiveInspector archiveInspector,
    IOptions<FileUploadOptions> fileUploadOptions,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<PublishAssetVersionCommandHandler> logger) : IRequestHandler<PublishAssetVersionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(PublishAssetVersionCommand request, CancellationToken cancellationToken)
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

        if (!AssetLicenseCatalog.TryParseCode(request.Request.LicenseCode, out var licenseCode))
        {
            return ResultError.Error<Guid>(ErrorCodes.ERR_LICENSE_CODE_INVALID);
        }

        var licenseTemplate = AssetLicenseCatalog.Get(licenseCode);

        var asset = await assetStore.GetById(request.AssetId, cancellationToken);
        if (asset is null || asset.DeletedAt.HasValue)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (asset.AuthorId != request.AuthorId)
        {
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
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
            logger.LogWarning(ex, "Archive rejected for version publish by {AuthorId} on asset {AssetId}", request.AuthorId, request.AssetId);
            return ResultError.Error<Guid>(ErrorCodes.ERR_ARCHIVE_REJECTED);
        }

        var versionId = Guid.NewGuid();
        var storageKey = $"assets/{request.AuthorId}/{request.AssetId}/{versionId}{matchedExtension}";
        var ciphertextLength = encryptionService.ComputeCiphertextLength(request.FileLength);

        string sha256Hex;
        try
        {
            sha256Hex = await EncryptAndUpload(request.FileContent, storageKey, ciphertextLength, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Encrypt/upload failed for asset {AssetId} version {VersionId}", request.AssetId, versionId);
            return ResultError.Error<Guid>(ErrorCodes.ERR_ASSET_UPLOAD_FAILED);
        }

        var now = DateTimeOffset.UtcNow;
        var draft = new AssetVersion
        {
            Id = versionId,
            AssetId = request.AssetId,
            VersionNumber = 0, // Set by PublishNextVersion.
            IsCurrent = false, // Set by PublishNextVersion.
            StorageKey = storageKey,
            FileName = displayFileName,
            ContentLength = request.FileLength,
            ContentSha256 = sha256Hex,
            ReleaseNotes = request.Request.ReleaseNotes,
            LicenseCode = licenseCode,
            LicenseTemplateVersion = licenseTemplate.TemplateVersion,
            LicenseDisplayName = licenseTemplate.DisplayName,
            LicenseTerms = licenseTemplate.TermsPlainText,
            CreatedAt = now
        };

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                await assetStore.PublishNextVersion(request.AssetId, request.AuthorId, draft, ct);

                await auditWriter.Write(new AuditEvent(
                    AuditActions.ASSET_VERSION_PUBLISH,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.ASSET,
                    request.AssetId.ToString(),
                    new Dictionary<string, object?>
                    {
                        ["versionId"] = versionId.ToString(),
                        ["licenseCode"] = licenseCode.ToString()
                    }), ct);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AssetNotFoundException)
        {
            await DeleteOrphan(storageKey, cancellationToken);
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }
        catch (UnauthorizedAccessException)
        {
            await DeleteOrphan(storageKey, cancellationToken);

            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DB publish failed for asset {AssetId} version {VersionId}; removing orphan from storage", request.AssetId, versionId);
            await DeleteOrphan(storageKey, cancellationToken);

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
            logger.LogWarning(ex, "Cache invalidation failed after publishing version for asset {AssetId}", request.AssetId);
        }

        logger.LogInformation("Version {VersionId} published for asset {AssetId} by {AuthorId}", versionId, request.AssetId, request.AuthorId);
        return Result.Success(versionId);
    }

    private async Task<string> EncryptAndUpload(
        Stream plain,
        string storageKey,
        long ciphertextLength,
        CancellationToken cancellationToken)
    {
        await using var hashingStream = new PlaintextHashObservingStream(plain);

        var pipe = new Pipe();
        Exception? encryptError = null;
        Exception? uploadError = null;

        var encryptTask = Task.Run(async () =>
        {
            try
            {
                await using var writerStream = pipe.Writer.AsStream(leaveOpen: true);
                await encryptionService.Encrypt(hashingStream, writerStream, cancellationToken).ConfigureAwait(false);
                hashingStream.FinalizeHash();
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

        return hashingStream.HashHex;
    }

    private async Task DeleteOrphan(string storageKey, CancellationToken cancellationToken)
    {
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
            logger.LogWarning(delEx, "Storage delete failed for orphan key {Key}", storageKey);
        }
    }
}
