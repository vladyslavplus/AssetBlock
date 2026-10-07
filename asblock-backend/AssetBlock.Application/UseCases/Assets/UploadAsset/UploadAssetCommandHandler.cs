using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.UploadAsset;

internal sealed class UploadAssetCommandHandler(
    ICategoryStore categoryStore,
    IAssetStore assetStore,
    ITagStore tagStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    IAssetEncryptUploadService encryptUploadService,
    IAssetProcessingJobStore processingJobStore,
    IModerationFoundationStore moderationFoundationStore,
    IOptions<FileUploadOptions> fileUploadOptions,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<UploadAssetCommandHandler> logger,
    TimeProvider? timeProvider = null) : IRequestHandler<UploadAssetCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(UploadAssetCommand request, CancellationToken cancellationToken)
    {
        FileUploadOptions uploadOpts = fileUploadOptions.Value;
        var displayFileName = uploadOpts.NormalizeDisplayFileName(request.FileName);
        _ = uploadOpts.TryMatchAllowedExtension(displayFileName, out var matchedExtension);
        _ = AssetLicenseCatalog.TryParseCode(request.Request.LicenseCode, out AssetLicenseCode licenseCode);
        AssetLicenseTemplate licenseTemplate = AssetLicenseCatalog.Get(licenseCode);

        Category? category = await categoryStore.GetById(request.Request.CategoryId, cancellationToken);
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

        // Upload-into-draft: the client names an existing owned pre-upload workspace; identity,
        // ownership, undeleted asset, and the client revision are verified before storage.
        Guid assetId;
        SellerDraftSnapshotDto? draftSnapshot = null;
        if (request.Request.WorkspaceId is { } workspaceId)
        {
            draftSnapshot = await moderationFoundationStore.GetOwnerDraftSnapshotByWorkspace(
                workspaceId, request.AuthorId, cancellationToken);
            if (draftSnapshot is null)
            {
                return Result.NotFound(ErrorCodes.ERR_ASSET_DRAFT_NOT_FOUND);
            }

            if (draftSnapshot.WorkspaceRevision != request.Request.ExpectedWorkspaceRevision)
            {
                return Result.Conflict(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
            }

            assetId = draftSnapshot.AssetId;
        }
        else
        {
            assetId = Guid.NewGuid();
        }

        var versionId = Guid.NewGuid();
        var storageKey = $"assets/{request.AuthorId}/{assetId}/{versionId}{matchedExtension}";
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
            logger.LogError(ex, "Encrypt/upload failed for asset {AssetId}", assetId);
            return ResultError.Error<Guid>(ErrorCodes.ERR_ASSET_UPLOAD_FAILED);
        }

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var version = new AssetVersion
        {
            Id = versionId,
            AssetId = assetId,
            VersionNumber = 1,
            IsCurrent = false,
            StorageKey = storageKey,
            FileName = displayFileName,
            ContentLength = request.FileLength,
            ContentSha256 = sha256Hex,
            ReleaseNotes = AssetVersionDefaults.INITIAL_RELEASE_NOTES,
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
                if (draftSnapshot is null)
                {
                    // Legacy create-and-upload: a fresh asset, its pre-upload workspace, and the
                    // initial metadata revision are created together before the version binds.
                    var asset = new Asset
                    {
                        Id = assetId,
                        AuthorId = request.AuthorId,
                        CategoryId = request.Request.CategoryId,
                        Title = request.Request.Title,
                        Description = request.Request.Description,
                        Price = request.Request.Price,
                        DownloadLimitPerHour = request.Request.DownloadLimitPerHour,
                        CreatedAt = now
                    };

                    await assetStore.AddWithVersion(asset, version, existingTags, ct);

                    AssetDraftWorkspaceSnapshot workspace = await moderationFoundationStore.EnsurePreUploadWorkspace(assetId, request.AuthorId, ct);

                    var material = new SellerDraftMaterialPayload(
                        request.Request.Title,
                        request.Request.Description,
                        request.Request.CategoryId,
                        existingTags?.Select(t => t.Name).ToList() ?? []);
                    var payloadJson = DraftPayloadJson.Serialize(material);
                    var digest = DraftPayloadJson.ComputeDigest(payloadJson);

                    ModerationDraftSaveResult revision = await moderationFoundationStore.SaveDraftRevision(
                        new DraftRevisionSaveRequest(
                            request.AuthorId,
                            assetId,
                            AssetVersionId: null,
                            ModerationOperationKinds.DRAFT_SAVE,
                            Guid.NewGuid(),
                            digest,
                            payloadJson,
                            SellerDraftLimits.MATERIAL_METADATA_SCHEMA_VERSION,
                            digest,
                            workspace.WorkspaceRevision),
                        ct);

                    if (revision.Status != ModerationDraftSaveStatus.SUCCEEDED)
                    {
                        throw new AssetDraftWorkspaceStaleException();
                    }

                    VersionAttachResult attach = await moderationFoundationStore.AttachUploadedVersionToWorkspace(
                        assetId, versionId, request.AuthorId, workspace.WorkspaceId, revision.WorkspaceRevision, ct);
                    if (attach.Status != VersionAttachStatus.ATTACHED)
                    {
                        throw new AssetDraftWorkspaceStaleException();
                    }

                    await EnqueueInspectionJob(assetId, versionId, ct);
                    await WriteCreateAudit(assetId, versionId, licenseCode, request.Request.CategoryId, existingTags, ct);
                }
                else
                {
                    // Draft branch: the asset, material heads, and declaration already exist; the
                    // bytes bind to the named workspace under the same post-storage recheck.
                    await assetStore.CreateNextCandidateVersion(assetId, request.AuthorId, version, ct);

                    VersionAttachResult attach = await moderationFoundationStore.AttachUploadedVersionToWorkspace(
                        assetId, versionId, request.AuthorId, request.Request.WorkspaceId!.Value, request.Request.ExpectedWorkspaceRevision!.Value, ct);
                    if (attach.Status != VersionAttachStatus.ATTACHED)
                    {
                        throw new AssetDraftWorkspaceStaleException();
                    }

                    if (existingTags is not null)
                    {
                        // Upload-form tags stay authoritative for the material head of the new
                        // version workspace; the attach already copied the pre-upload heads.
                        SellerDraftMaterialPayload taggedMaterial = draftSnapshot.Material with
                        {
                            Tags = existingTags.Select(t => t.Name).ToList()
                        };
                        var payloadJson = DraftPayloadJson.Serialize(taggedMaterial);
                        var digest = DraftPayloadJson.ComputeDigest(payloadJson);
                        var requestDigest = DraftPayloadJson.ComputeRequestDigest(
                            assetId,
                            versionId,
                            ModerationOperationKinds.DRAFT_SAVE,
                            attach.VersionWorkspace!.WorkspaceRevision,
                            payloadJson);

                        ModerationDraftSaveResult revision = await moderationFoundationStore.SaveDraftRevision(
                            new DraftRevisionSaveRequest(
                                request.AuthorId,
                                assetId,
                                versionId,
                                ModerationOperationKinds.DRAFT_SAVE,
                                Guid.NewGuid(),
                                requestDigest,
                                payloadJson,
                                SellerDraftLimits.MATERIAL_METADATA_SCHEMA_VERSION,
                                digest,
                                attach.VersionWorkspace.WorkspaceRevision),
                            ct);

                        if (revision.Status != ModerationDraftSaveStatus.SUCCEEDED)
                        {
                            throw new AssetDraftWorkspaceStaleException();
                        }
                    }

                    await EnqueueInspectionJob(assetId, versionId, ct);
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.ASSET_VERSION_PUBLISH,
                        AuditOutcome.SUCCESS,
                        AuditResourceTypes.ASSET,
                        assetId.ToString(),
                        new Dictionary<string, object?>
                        {
                            ["versionId"] = versionId.ToString(),
                            ["licenseCode"] = licenseCode.ToString()
                        }), ct);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Do not delete storage: commit outcome may be indeterminate (cancel or
            // connection drop after commit). Orphan blobs are cleaned later by
            // StorageOrphanCleanupWorker when no DB row references the key.
            throw;
        }
        catch (AssetDraftWorkspaceStaleException)
        {
            // Guaranteed pre-commit failure: nothing was attached, clean the uploaded object.
            await TryDeletePartialObject(storageKey);
            return Result.Conflict(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
        }
        catch (Exception ex)
        {
            // Same indeterminate-commit risk as cancellation for generic DB/network errors.
            logger.LogWarning(
                ex,
                "DB add failed for asset {AssetId}; leaving storage object {Key} for orphan cleanup if uncommitted",
                assetId,
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
            logger.LogWarning(ex, "Cache invalidation failed after upload {AssetId}", assetId);
        }

        logger.LogInformation("Asset uploaded successfully {AssetId} by {AuthorId}", assetId, request.AuthorId);
        return Result.Success(assetId);
    }

    private async Task EnqueueInspectionJob(Guid assetId, Guid versionId, CancellationToken ct)
    {
        // Enqueue archive inspection job atomically with the version insert.
        // Keeps the version from staying permanently in PENDING_INSPECTION.
        await processingJobStore.Enqueue(
            assetId,
            versionId,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            definitionVersion: AssetProcessingDefaults.DEFINITION_VERSION,
            initialDelay: TimeSpan.Zero,
            payload: new ArchiveInspectionPayload(),
            traceParent: null,
            ct);
    }

    private async Task WriteCreateAudit(Guid assetId, Guid versionId, AssetLicenseCode licenseCode, Guid categoryId, List<Tag>? existingTags, CancellationToken ct)
    {
        await auditWriter.Write(new AuditEvent(
            AuditActions.ASSET_CREATE,
            AuditOutcome.SUCCESS,
            AuditResourceTypes.ASSET,
            assetId.ToString(),
            new Dictionary<string, object?>
            {
                ["categoryId"] = categoryId.ToString(),
                ["versionId"] = versionId.ToString(),
                ["licenseCode"] = licenseCode.ToString(),
                ["tagCount"] = existingTags?.Count ?? 0
            }), ct);
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
            logger.LogWarning(delEx, "Storage delete failed for {Key}", storageKey);
        }
    }
}
