using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace AssetBlock.Infrastructure.HostedServices.AssetProcessing.Handlers;

public sealed class ArchiveInspectionJobHandler(
    IAssetStore assetStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    IArchiveSafetyInspector archiveInspector,
    IAssetProcessingLifecycleStore lifecycleStore,
    IOptions<AssetProcessingOptions> processingOptions,
    ILogger<ArchiveInspectionJobHandler> logger)
    : IAssetProcessingJobHandler<ArchiveInspectionPayload, ArchiveInspectionResult>
{
    public async Task<AssetProcessingJobOutcome> Process(
        AssetProcessingJobContext<ArchiveInspectionPayload> context,
        CancellationToken cancellationToken)
    {
        var version = await assetStore.GetVersion(context.AssetId, context.AssetVersionId, cancellationToken);
        if (version is null)
        {
            logger.LogWarning("Version {VersionId} not found for job {JobId}", context.AssetVersionId, context.JobId);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.VERSION_NOT_FOUND,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.VERSION_NOT_FOUND));
        }

        ArchiveSafetyResult safetyResult;
        try
        {
            ArchiveSafetyResult? inspected = null;
            await assetStorageService.OpenRead(
                version.StorageKey,
                async (encryptedStream, ct) =>
                {
                    inspected = await DecryptedContentPipeline.Run(
                        encryptedStream,
                        encryptionService,
                        (plain, innerCt) => archiveInspector.Inspect(plain, version.FileName, innerCt),
                        ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            safetyResult = inspected
                ?? throw new InvalidOperationException("Archive inspection did not produce a result.");
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "Failed to decrypt archive for version {VersionId}", context.AssetVersionId);
            var transitioned = await lifecycleStore.TransitionArchiveInspectionRejected(
                context.JobId,
                context.LeaseToken,
                context.AssetId,
                context.AssetVersionId,
                ErrorCodes.ARCHIVE_CORRUPT,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT),
                cancellationToken);

            return transitioned
                ? AssetProcessingJobOutcome.CommittedFailed()
                : AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.LEASE_LOST,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Storage read network failure for version {VersionId}", context.AssetVersionId);
            return AssetProcessingJobOutcome.Retryable(
                ErrorCodes.STORAGE_UNAVAILABLE,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.STORAGE_UNAVAILABLE),
                processingOptions.Value.InitialRetryDelay);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "I/O failure while streaming archive for version {VersionId}", context.AssetVersionId);
            return AssetProcessingJobOutcome.Retryable(
                ErrorCodes.STORAGE_IO_ERROR,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.STORAGE_IO_ERROR),
                processingOptions.Value.InitialRetryDelay);
        }

        if (safetyResult.IsSafe)
        {
            var analysisResult = new ArchiveInspectionResult(
                FileCount: safetyResult.FileCount,
                TotalSizeUncompressed: safetyResult.TotalExpandedBytes);

            var analysisRecord = new BoundedArchiveAnalysisRecord(
                FileCount: safetyResult.FileCount,
                TotalExpandedBytes: safetyResult.TotalExpandedBytes,
                ReadmeContent: safetyResult.ReadmeContent,
                ManifestMetadata: safetyResult.ManifestMetadata);

            var transitioned = await lifecycleStore.TransitionArchiveInspectionAccepted(
                context.JobId,
                context.LeaseToken,
                context.AssetId,
                context.AssetVersionId,
                analysisResult,
                analysisRecord,
                cancellationToken);

            return transitioned
                ? AssetProcessingJobOutcome.CommittedSucceeded()
                : AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.LEASE_LOST,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
        }

        var errorCode = safetyResult.ErrorCode ?? ErrorCodes.ARCHIVE_UNSAFE;
        var errorSummary = ErrorCodesToErrorMessages.GetMessage(errorCode);
        var rejected = await lifecycleStore.TransitionArchiveInspectionRejected(
            context.JobId,
            context.LeaseToken,
            context.AssetId,
            context.AssetVersionId,
            errorCode,
            errorSummary,
            cancellationToken);

        return rejected
            ? AssetProcessingJobOutcome.CommittedFailed()
            : AssetProcessingJobOutcome.Retryable(
                ErrorCodes.LEASE_LOST,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
    }
}
