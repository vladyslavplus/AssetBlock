using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.PublishAssetVersion;

internal sealed class PublishAssetVersionCommandHandler(
    IAssetStore assetStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    IAssetEncryptUploadService encryptUploadService,
    IAssetProcessingJobStore processingJobStore,
    IOptions<FileUploadOptions> fileUploadOptions,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<PublishAssetVersionCommandHandler> logger,
    TimeProvider? timeProvider = null) : IRequestHandler<PublishAssetVersionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(PublishAssetVersionCommand request, CancellationToken cancellationToken)
    {
        FileUploadOptions uploadOpts = fileUploadOptions.Value;
        var displayFileName = uploadOpts.NormalizeDisplayFileName(request.FileName);
        _ = uploadOpts.TryMatchAllowedExtension(displayFileName, out var matchedExtension);
        _ = AssetLicenseCatalog.TryParseCode(request.Request.LicenseCode, out AssetLicenseCode licenseCode);
        AssetLicenseTemplate licenseTemplate = AssetLicenseCatalog.Get(licenseCode);

        Asset? asset = await assetStore.GetById(request.AssetId, cancellationToken);
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
            sha256Hex = await encryptUploadService.EncryptAndUpload(request.FileContent, storageKey, ciphertextLength, cancellationToken);
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

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
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
