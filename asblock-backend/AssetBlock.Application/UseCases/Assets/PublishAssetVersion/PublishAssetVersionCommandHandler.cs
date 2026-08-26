using System.IO.Pipelines;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Ardalis.Result;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.PublishAssetVersion;

internal sealed class PublishAssetVersionCommandHandler(
    IAssetStore assetStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    IAssetProcessingJobStore processingJobStore,
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
            await TryDeletePartialObject(storageKey);
            throw;
        }
        catch (Exception ex)
        {
            await TryDeletePartialObject(storageKey);
            logger.LogError(ex, "Encrypt/upload failed for asset {AssetId} version {VersionId}", request.AssetId, versionId);
            return ResultError.Error<Guid>(ErrorCodes.ERR_ASSET_UPLOAD_FAILED);
        }

        var now = DateTimeOffset.UtcNow;
        var draft = new AssetVersion
        {
            Id = versionId,
            AssetId = request.AssetId,
            VersionNumber = 0, // Set by CreateNextCandidateVersion.
            IsCurrent = false, // Set by CreateNextCandidateVersion.
            StorageKey = storageKey,
            FileName = displayFileName,
            ContentLength = request.FileLength,
            ContentSha256 = sha256Hex,
            ReleaseNotes = request.Request.ReleaseNotes.Trim(),
            LicenseCode = licenseCode,
            LicenseTemplateVersion = licenseTemplate.TemplateVersion,
            LicenseDisplayName = licenseTemplate.DisplayName,
            LicenseTerms = licenseTemplate.TermsPlainText,
            ProcessingStatus = AssetVersionProcessingStatus.PENDING_INSPECTION,
            ProcessingUpdatedAt = now,
            CreatedAt = now
        };

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                await assetStore.CreateNextCandidateVersion(request.AssetId, request.AuthorId, draft, ct);

                // Enqueue archive inspection job atomically with the version insert.
                // Keeps the version from staying permanently in PENDING_INSPECTION.
                await processingJobStore.Enqueue(
                    request.AssetId,
                    versionId,
                    AssetProcessingJobType.ARCHIVE_INSPECTION,
                    definitionVersion: AssetProcessingDefaults.DEFINITION_VERSION,
                    initialDelay: TimeSpan.Zero,
                    payload: new ArchiveInspectionPayload(),
                    traceParent: null,
                    ct);

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
            // Do not delete storage: commit outcome may be indeterminate.
            throw;
        }
        catch (AssetNotFoundException)
        {
            // Guaranteed pre-commit domain failure from PublishNextVersion.
            await TryDeletePartialObject(storageKey);
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }
        catch (UnauthorizedAccessException)
        {
            // Guaranteed pre-commit domain failure from PublishNextVersion.
            await TryDeletePartialObject(storageKey);

            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }
        catch (Exception ex)
        {
            // Generic DB/network errors after CommitAsync may mean the row is already committed.
            logger.LogWarning(
                ex,
                "DB publish failed for asset {AssetId} version {VersionId}; leaving storage object {Key} for orphan cleanup if uncommitted",
                request.AssetId,
                versionId,
                storageKey);
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

    /// <summary>
    /// Best-effort delete of the attempted UUID key after encrypt/upload or DB failure.
    /// Uses a short independent token so a cancelled request cannot block cleanup.
    /// </summary>
    private async Task TryDeletePartialObject(string storageKey)
    {
        try
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await assetStorageService.Delete(storageKey, cleanupCts.Token);
        }
        catch (Exception delEx)
        {
            logger.LogWarning(delEx, "Storage delete failed for orphan key {Key}", storageKey);
        }
    }
}
