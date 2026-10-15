using System.Buffers;
using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.HostedServices.AssetProcessing.Handlers;

public sealed class ArchiveInspectionJobHandler(
    IAssetStore assetStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    IArchiveSafetyInspector archiveInspector,
    IAssetProcessingLifecycleStore lifecycleStore,
    ISecurityProvenanceStore securityProvenance,
    IOptions<AssetProcessingOptions> processingOptions,
    ILogger<ArchiveInspectionJobHandler> logger)
    : IAssetProcessingJobHandler<ArchiveInspectionPayload, ArchiveInspectionResult>
{
    public async Task<AssetProcessingJobOutcome> Process(
        AssetProcessingJobContext<ArchiveInspectionPayload> context,
        CancellationToken cancellationToken)
    {
        AssetVersion? version = await assetStore.GetVersion(context.AssetId, context.AssetVersionId, cancellationToken);
        if (version is null)
        {
            logger.LogWarning("Version {VersionId} not found for job {JobId}", context.AssetVersionId, context.JobId);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.VERSION_NOT_FOUND,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.VERSION_NOT_FOUND));
        }

        var isRecheck = context.SecurityRecheckId is not null;

        ArchiveSafetyResult safetyResult;
        StrictDecryptedContentPipeline.StrictDecryptedContent? observedContent;
        bool fullyVerified;
        try
        {
            StrictDecryptedContentPipeline.VerifiedRun<ArchiveSafetyResult>? run = null;
            await assetStorageService.OpenRead(
                version.StorageKey,
                async (encryptedStream, ct) =>
                {
                    run = await StrictDecryptedContentPipeline.RunVerified(
                        encryptedStream,
                        encryptionService,
                        async (plain, innerCt) =>
                        {
                            ArchiveSafetyResult result = await archiveInspector
                                .Inspect(plain, version.FileName, innerCt)
                                .ConfigureAwait(false);

                            // A safe archive verdict is admissible only after the raw archive
                            // stream has been consumed to EOF. Some archive readers stop after
                            // the directory/end records, leaving authenticated trailing bytes
                            // unread. Drain through the strict hashing/decryption reader so its
                            // proof still covers every advertised byte and AEAD end marker.
                            if (result.IsSafe)
                            {
                                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                                try
                                {
                                    while (await plain.ReadAsync(buffer.AsMemory(), innerCt).ConfigureAwait(false) != 0)
                                    {
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(buffer);
                                }
                            }

                            return result;
                        },
                        ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (run is null)
            {
                throw new InvalidOperationException("Archive inspection did not produce a result.");
            }

            safetyResult = run.Result;
            observedContent = run.Content;
            fullyVerified = run.FullyVerified;
        }
        catch (CryptographicException ex)
        {
            // Producer integrity failure (AEAD/truncation from the decryptor). This is proven
            // byte corruption, distinct from an inspector that merely stopped reading.
            logger.LogWarning(ex, "Failed to decrypt archive for version {VersionId}", context.AssetVersionId);
            return await TransitionDecryptFailure(context, isRecheck, cancellationToken);
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

        // An unverified run carries no proof. An unsafe verdict still stands on its own
        // classification; a safe verdict over a partial stream proves nothing and stays
        // operational, never fabricated into evidence and never reported as corruption.
        if (!fullyVerified || observedContent is null)
        {
            if (!safetyResult.IsSafe)
            {
                return await TransitionUnsafe(
                    context, isRecheck,
                    safetyResult.ErrorCode ?? ErrorCodes.ARCHIVE_UNSAFE,
                    cancellationToken);
            }

            logger.LogWarning(
                "Archive inspection for version {VersionId} returned safe without fully consuming its input.",
                context.AssetVersionId);
            return await TransitionEvidenceIncomplete(context, isRecheck, cancellationToken);
        }

        // Integrity: only a fully verified stream over the exact advertised bytes is inspection
        // input. Anything else is never trusted evidence.
        if (!string.Equals(observedContent.ContentSha256, version.ContentSha256, StringComparison.Ordinal)
            || observedContent.ByteCount != version.ContentLength)
        {
            logger.LogWarning(
                "Archive inspection input for version {VersionId} does not match the advertised content hash or length.",
                context.AssetVersionId);
            return await TransitionIntegrityMismatch(context, isRecheck, cancellationToken);
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

            // The observed proof is persisted atomically with this completion: the verified hash,
            // the verified byte scope and the actual inspector implementation that ran.
            var evidence = new ArchiveValidationEvidence(
                observedContent.ContentSha256,
                safetyResult.FileCount,
                safetyResult.TotalExpandedBytes,
                observedContent.ByteCount,
                ScopeComplete: true,
                ImplementationIdentity: ArchiveSafetyInspector.IMPLEMENTATION_IDENTITY);

            if (isRecheck)
            {
                SecurityRecheckTransitionResult accepted = await securityProvenance.TransitionRecheckArchiveAccepted(
                    context.JobId,
                    context.LeaseToken,
                    context.AssetId,
                    context.AssetVersionId,
                    evidence,
                    cancellationToken);
                return accepted.Committed
                    ? AssetProcessingJobOutcome.CommittedSucceeded()
                    : AssetProcessingJobOutcome.Retryable(
                        ErrorCodes.LEASE_LOST,
                        ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
            }

            var transitioned = await lifecycleStore.TransitionArchiveInspectionAccepted(
                context.JobId,
                context.LeaseToken,
                context.AssetId,
                context.AssetVersionId,
                analysisResult,
                analysisRecord,
                observedEvidence: evidence,
                observedDefinitionVersion: context.DefinitionVersion,
                cancellationToken: cancellationToken);

            return transitioned
                ? AssetProcessingJobOutcome.CommittedSucceeded()
                : AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.LEASE_LOST,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
        }

        return await TransitionUnsafe(
            context, isRecheck,
            safetyResult.ErrorCode ?? ErrorCodes.ARCHIVE_UNSAFE,
            cancellationToken);
    }

    private async Task<AssetProcessingJobOutcome> TransitionDecryptFailure(
        AssetProcessingJobContext<ArchiveInspectionPayload> context,
        bool isRecheck,
        CancellationToken cancellationToken)
    {
        if (isRecheck)
        {
            // Genuine decryptor failure on a READY version quarantines those exact bytes.
            SecurityRecheckTransitionResult quarantined = await securityProvenance.TransitionRecheckIntegrityMismatch(
                context.JobId,
                context.LeaseToken,
                context.AssetId,
                context.AssetVersionId,
                ErrorCodes.ARCHIVE_CORRUPT,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT),
                cancellationToken);
            return quarantined.Committed
                ? AssetProcessingJobOutcome.CommittedFailed()
                : AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.LEASE_LOST,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
        }

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

    private async Task<AssetProcessingJobOutcome> TransitionIntegrityMismatch(
        AssetProcessingJobContext<ArchiveInspectionPayload> context,
        bool isRecheck,
        CancellationToken cancellationToken)
    {
        if (isRecheck)
        {
            // Observed bytes that are not the advertised version quarantine those exact bytes
            // as an integrity mismatch, never as a malware verdict.
            SecurityRecheckTransitionResult quarantined = await securityProvenance.TransitionRecheckIntegrityMismatch(
                context.JobId,
                context.LeaseToken,
                context.AssetId,
                context.AssetVersionId,
                ErrorCodes.ARCHIVE_CORRUPT,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT),
                cancellationToken);
            return quarantined.Committed
                ? AssetProcessingJobOutcome.CommittedFailed()
                : AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.LEASE_LOST,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
        }

        var mismatchRejected = await lifecycleStore.TransitionArchiveInspectionRejected(
            context.JobId,
            context.LeaseToken,
            context.AssetId,
            context.AssetVersionId,
            ErrorCodes.ARCHIVE_CORRUPT,
            ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT),
            cancellationToken);

        return mismatchRejected
            ? AssetProcessingJobOutcome.CommittedFailed()
            : AssetProcessingJobOutcome.Retryable(
                ErrorCodes.LEASE_LOST,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
    }

    private async Task<AssetProcessingJobOutcome> TransitionUnsafe(
        AssetProcessingJobContext<ArchiveInspectionPayload> context,
        bool isRecheck,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var errorSummary = ErrorCodesToErrorMessages.GetMessage(errorCode);
        if (isRecheck)
        {
            SecurityRecheckTransitionResult unsafeRecheck = await securityProvenance.TransitionRecheckArchiveUnsafe(
                context.JobId,
                context.LeaseToken,
                context.AssetId,
                context.AssetVersionId,
                errorCode,
                errorSummary,
                cancellationToken);
            return unsafeRecheck.Committed
                ? AssetProcessingJobOutcome.CommittedFailed()
                : AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.LEASE_LOST,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
        }

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

    private async Task<AssetProcessingJobOutcome> TransitionEvidenceIncomplete(
        AssetProcessingJobContext<ArchiveInspectionPayload> context,
        bool isRecheck,
        CancellationToken cancellationToken)
    {
        if (isRecheck)
        {
            SecurityRecheckTransitionResult failed = await securityProvenance.TransitionRecheckFailed(
                context.JobId,
                context.LeaseToken,
                context.AssetId,
                context.AssetVersionId,
                ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE),
                cancellationToken);
            return failed.Committed
                ? AssetProcessingJobOutcome.CommittedFailed()
                : AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.LEASE_LOST,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
        }

        return AssetProcessingJobOutcome.Retryable(
            ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE,
            ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE),
            processingOptions.Value.InitialRetryDelay);
    }
}
