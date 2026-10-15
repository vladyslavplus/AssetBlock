using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.HostedServices.AssetProcessing.Handlers;

/// <summary>
/// Runs one admitted analysis against the external service. The archive is streamed straight from the
/// decrypted storage pipeline and only a fully authenticated end-to-end stream is analysis input. The
/// execution disposition is reported with every outcome so the worker returns shared capacity on real
/// evidence instead of a bare database status.
/// </summary>
public sealed class CodeAnalysisJobHandler(
    IAssetStore assetStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    ICodeAnalysisClient client,
    IAnalysisReservationReader reservationReader,
    ICodeAnalysisReportFinalizer reportFinalizer,
    IOptions<AssetProcessingOptions> processingOptions,
    ILogger<CodeAnalysisJobHandler> logger)
    : IAssetProcessingJobHandler<CodeAnalysisPayload, CodeAnalysisResult>
{
    public async Task<AssetProcessingJobOutcome> Process(
        AssetProcessingJobContext<CodeAnalysisPayload> context,
        CancellationToken cancellationToken)
    {
        AssetVersion? version = await assetStore.GetVersion(context.AssetId, context.AssetVersionId, cancellationToken);
        if (version is null)
        {
            logger.LogWarning("Version {VersionId} not found for job {JobId}", context.AssetVersionId, context.JobId);
            return Refused(
                AssetProcessingJobOutcome.Terminal(
                    ErrorCodes.VERSION_NOT_FOUND,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.VERSION_NOT_FOUND)));
        }

        if (version.SafetyRestriction != AssetVersionSafetyRestriction.NONE)
        {
            // A quarantined exact version never becomes analysis input.
            return Refused(
                AssetProcessingJobOutcome.Terminal(
                    ErrorCodes.ANALYSIS_RESPONSE_INVALID,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID)));
        }

        AnalysisReservationBinding? binding = await reservationReader.Read(context, cancellationToken);
        if (binding is null)
        {
            // The claim is no longer provable, so nothing may be sent against it.
            logger.LogWarning("Job {JobId} has no provable analysis reservation.", context.JobId);
            return Refused(
                AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.ANALYSIS_CAPACITY_UNAVAILABLE,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_CAPACITY_UNAVAILABLE),
                    processingOptions.Value.InitialRetryDelay));
        }

        var metadata = new CodeAnalysisMetadataRequest(
            context.Payload.AnalysisRunId.ToString("D"),
            context.AssetId.ToString("D"),
            context.AssetVersionId.ToString("D"),
            context.Payload.ContentSha256,
            context.Payload.InputDigest,
            binding.PolicyKey,
            binding.DeclarationDigest,
            binding.MaterialDigest,
            new CodeAnalysisReservation(
                binding.ServiceKey,
                binding.SlotNumber,
                binding.ReservationGeneration,
                binding.TransportLeaseToken,
                FormatDeadline(binding.HardDeadlineUtc)),
            new CodeAnalysisSecurityEvidence(
                context.Payload.ContentSha256,
                context.Payload.ArchiveEvidenceId.ToString("D"),
                context.Payload.MalwareEvidenceId.ToString("D"),
                context.Payload.SecurityGeneration),
            binding.DeclaredLicenses,
            binding.Reuse);

        CodeAnalysisServiceResponse response;
        try
        {
            CodeAnalysisServiceResponse observed = await StreamVerifiedArchive(
                version, metadata, cancellationToken).ConfigureAwait(false);
            response = observed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CryptographicException ex)
        {
            // A real AEAD/hash/integrity failure is never a busy deferral. Quarantine the exact
            // version under the live lease; a lost lease authorizes no unfenced mutation.
            logger.LogWarning(ex, "Analysis input for version {VersionId} was not fully authenticated.", context.AssetVersionId);
            AssetProcessingJobOutcome quarantined = await reportFinalizer.QuarantineIntegrity(
                context.JobId, context.LeaseToken, ErrorCodes.ARCHIVE_CORRUPT, cancellationToken).ConfigureAwait(false);
            return AssetProcessingJobOutcome.External(quarantined, AnalysisExecutionDisposition.UNCONFIRMED);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "I/O failure streaming analysis input for version {VersionId}", context.AssetVersionId);
            return AssetProcessingJobOutcome.External(
                AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.STORAGE_IO_ERROR,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.STORAGE_IO_ERROR),
                    processingOptions.Value.InitialRetryDelay),
                AnalysisExecutionDisposition.UNCONFIRMED);
        }

        return await Interpret(context, response, binding.ReservationGeneration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps the observed service response onto an outcome. Only the service can tell us whether work
    /// started; every branch here carries that observation instead of assuming it.
    /// </summary>
    private async Task<AssetProcessingJobOutcome> Interpret(
        AssetProcessingJobContext<CodeAnalysisPayload> context,
        CodeAnalysisServiceResponse response,
        int reservationGeneration,
        CancellationToken cancellationToken)
    {
        if (response.Disposition == AnalysisExecutionDisposition.NOT_STARTED)
        {
            // SERVICE_BUSY is the only refusal that can change on its own. Waiting behind it must not
            // consume an attempt, so the claim is refunded rather than retried.
            if (string.Equals(response.ErrorCode, ErrorCodes.ANALYSIS_SERVICE_BUSY, StringComparison.Ordinal))
            {
                return AssetProcessingJobOutcome.PreStartDeferred(
                    ErrorCodes.ANALYSIS_SERVICE_BUSY,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_SERVICE_BUSY),
                    reservationGeneration);
            }

            // A deterministic refusal cannot succeed unchanged, so it ends the attempt chain instead
            // of looping through the busy refund path forever.
            return Refused(
                AssetProcessingJobOutcome.Terminal(
                    response.ErrorCode ?? ErrorCodes.ANALYSIS_RESPONSE_INVALID,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID)));
        }

        if (response.ResponseBody is null)
        {
            // Ambiguous: the execution may still be running, so the reservation stays fenced.
            return AssetProcessingJobOutcome.External(
                AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.ANALYSIS_RESPONSE_INVALID,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID),
                    processingOptions.Value.InitialRetryDelay),
                AnalysisExecutionDisposition.UNCONFIRMED);
        }

        // The execution is confirmed finished and the response is complete. The leased
        // finalizer owns validation and admission in one commit; nothing is admitted here.
        // Its AtomicCommitted outcome lets the worker publish without a second transition.
        AssetProcessingJobOutcome finalized = await reportFinalizer.FinalizeConfirmed(
            context.JobId, context.LeaseToken, response.ResponseBody, cancellationToken).ConfigureAwait(false);
        return AssetProcessingJobOutcome.External(finalized, AnalysisExecutionDisposition.CONFIRMED_TERMINATED);
    }

    /// <summary>
    /// Streams the archive part directly from the decrypted storage pipeline. Nothing buffers the
    /// whole archive. The observed plaintext hash and byte count are the proof that the bytes sent
    /// were the bytes of the advertised exact version; comparing the two expected values instead
    /// would prove nothing.
    /// </summary>
    private async Task<CodeAnalysisServiceResponse> StreamVerifiedArchive(
        AssetVersion version,
        CodeAnalysisMetadataRequest metadata,
        CancellationToken cancellationToken)
    {
        CodeAnalysisServiceResponse? response = null;
        StrictDecryptedContentPipeline.VerifiedRun<CodeAnalysisServiceResponse>? run = null;

        await assetStorageService.OpenRead(
            version.StorageKey,
            async (encryptedStream, ct) =>
            {
                run = await StrictDecryptedContentPipeline.RunVerified(
                    encryptedStream,
                    encryptionService,
                    async (plain, innerCt) =>
                    {
                        response = await client.Send(
                            metadata,
                            async (target, archiveCt) => await plain.CopyToAsync(target, archiveCt),
                            innerCt).ConfigureAwait(false);
                        return response;
                    },
                    ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (response is not { } observed)
        {
            // The request never left this process, so no execution can have started.
            return new CodeAnalysisServiceResponse(
                AnalysisExecutionDisposition.NOT_STARTED, null, ErrorCodes.ANALYSIS_CAPACITY_UNAVAILABLE, 0);
        }

        if (observed.Disposition == AnalysisExecutionDisposition.NOT_STARTED)
        {
            // A refusal proven to precede acceptance legitimately leaves the archive unread, so
            // the run is unverified by construction. The typed verification state carries this;
            // no exception message is matched.
            return observed;
        }

        // The bytes were sent, so the analyzed input has to be proven locally before anything is
        // admitted from the response.
        if (run is not { FullyVerified: true, Content: { } content })
        {
            throw new CryptographicException("The analysis input stream was never verified end to end.");
        }

        if (!string.Equals(content.ContentSha256, version.ContentSha256, StringComparison.Ordinal)
            || content.ByteCount != version.ContentLength)
        {
            // The stored object is not the advertised exact version. This is a security failure of
            // the version itself, never an admission outcome and never a clean scanner result.
            throw new CryptographicException(
                "The decrypted analysis input does not match the advertised content hash or length of this version.");
        }

        return observed;
    }

    private static AssetProcessingJobOutcome Refused(AssetProcessingJobOutcome inner) =>
        AssetProcessingJobOutcome.External(inner, AnalysisExecutionDisposition.NOT_STARTED);

    private static string FormatDeadline(DateTimeOffset deadline) =>
        deadline.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);
}
