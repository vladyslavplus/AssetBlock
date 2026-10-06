using System.Diagnostics;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Domain.Core.Publication;
using AssetBlock.Infrastructure.Observability;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Publication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.HostedServices.AssetProcessing.Handlers;

public sealed class EmbeddingGenerationJobHandler(
    IAssetStore assetStore,
    ITextEmbeddingGenerator embeddingGenerator,
    IAssetEmbeddingFinalizer finalizer,
    IOptions<EmbeddingOptions> options,
    ApplicationDbContext dbContext,
    ILogger<EmbeddingGenerationJobHandler> logger)
    : IAssetProcessingJobHandler<EmbeddingGenerationPayload, EmbeddingGenerationResult>
{
    public async Task<AssetProcessingJobOutcome> Process(
        AssetProcessingJobContext<EmbeddingGenerationPayload> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EmbeddingOptions embeddingOptions = options.Value;
        if (!embeddingOptions.Enabled)
        {
            return AssetProcessingJobOutcome.Retryable(
                ErrorCodes.ERR_AI_DISABLED,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_AI_DISABLED),
                TimeSpan.FromMinutes(1));
        }

        var expectedModelKey = EmbeddingModelKey.Compute(embeddingOptions);
        if (!string.Equals(context.Payload.ModelKey, expectedModelKey, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Embedding job {JobId} model key {JobModelKey} does not match configured {ConfiguredModelKey}. Marking terminal.",
                context.JobId, context.Payload.ModelKey, expectedModelKey);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.INVALID_JOB_PAYLOAD,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.INVALID_JOB_PAYLOAD));
        }

        if (!string.Equals(context.Payload.ContentSchemaVersion, AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION, StringComparison.Ordinal))
        {
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.INVALID_JOB_PAYLOAD,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.INVALID_JOB_PAYLOAD));
        }

        Asset? asset = await assetStore.GetById(context.AssetId, includeDeleted: true, cancellationToken);
        if (asset is null || asset.DeletedAt != null)
        {
            logger.LogInformation("Asset is deleted or not found. Job {JobId} completed as no-op.", context.JobId);
            return await CompleteNoOp(context, cancellationToken);
        }

        if (asset.SearchRevision > context.Payload.TargetRevision)
        {
            logger.LogInformation("Asset search revision {CurrentRev} is newer than target {TargetRev}. Job {JobId} completed as no-op.",
                asset.SearchRevision, context.Payload.TargetRevision, context.JobId);
            return await CompleteNoOp(context, cancellationToken);
        }

        var approvedTarget = await PublicationEligibilityQuery.PublicCatalogAssets(dbContext)
            .Where(a => a.Id == context.AssetId)
            .Select(a => new
            {
                VersionId = a.CurrentPublicationSnapshot!.AssetVersionId,
                MetadataJson = a.CurrentPublicationSnapshot!.ApprovedMetadataJson,
                a.SearchRevision
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (approvedTarget is null
            || approvedTarget.VersionId != context.Payload.AssetVersionId
            || approvedTarget.VersionId != context.AssetVersionId
            || !ApprovedPublicationMetadata.TryReadPublicProjection(
                approvedTarget.MetadataJson,
                out ApprovedPublicationMetadata.PublicProjection publication))
        {
            logger.LogInformation("No approved public offering for embedding job {JobId}. Completed as no-op.", context.JobId);
            return await CompleteNoOp(context, cancellationToken);
        }

        var categoryName = await dbContext.Categories
            .AsNoTracking()
            .Where(c => c.Id == publication.CategoryId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken);
        if (categoryName is null)
        {
            logger.LogInformation("Approved category missing for embedding job {JobId}. Completed as no-op.", context.JobId);
            return await CompleteNoOp(context, cancellationToken);
        }

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(
            publication.Title,
            publication.Description,
            categoryName,
            publication.Tags);

        if (!string.Equals(canonical.ContentHash, context.Payload.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Canonical content hash mismatch. Job {JobId} completed as no-op.", context.JobId);
            return await CompleteNoOp(context, cancellationToken);
        }

        GeneratedEmbedding generated;
        var sw = Stopwatch.StartNew();
        using (AssetBlockDiagnostics.ActivitySource.StartActivity("embedding.document.generate"))
        {
            ModelVerificationResult availability = await embeddingGenerator.CheckModelAvailability(cancellationToken);
            if (!availability.IsAvailable)
            {
                logger.LogWarning("Embedding model availability check failed for job {JobId}: {Reason}. Will retry.",
                    context.JobId, availability.FailureReason);
                AssetBlockDiagnostics.RecordDocumentEmbedding(EmbeddingDiagnosticsOutcomes.FAILED, sw.Elapsed);
                return AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE));
            }

            try
            {
                generated = await embeddingGenerator.Generate(canonical.CanonicalText, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to generate embedding for job {JobId}. Will retry.", context.JobId);
                AssetBlockDiagnostics.RecordDocumentEmbedding(EmbeddingDiagnosticsOutcomes.FAILED, sw.Elapsed);
                return AssetProcessingJobOutcome.Retryable(
                    ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE));
            }
        }

        EmbeddingFinalizationStatus finalizationStatus = await finalizer.Finalize(new FinalizeEmbeddingParameters(
            context.JobId,
            context.LeaseToken,
            context.AssetId,
            approvedTarget.VersionId,
            approvedTarget.SearchRevision,
            canonical.ContentHash,
            expectedModelKey,
            embeddingOptions.Provider,
            embeddingOptions.Model,
            embeddingOptions.Revision,
            embeddingOptions.Digest,
            embeddingOptions.Dimension,
            AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION,
            generated.Vector), cancellationToken);

        if (finalizationStatus == EmbeddingFinalizationStatus.LEASE_LOST)
        {
            return AssetProcessingJobOutcome.Retryable(
                ErrorCodes.LEASE_LOST,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
        }

        AssetBlockDiagnostics.RecordDocumentEmbedding(
            finalizationStatus == EmbeddingFinalizationStatus.COMMITTED
                ? EmbeddingDiagnosticsOutcomes.SUCCESS
                : EmbeddingDiagnosticsOutcomes.NO_OP_STALE,
            sw.Elapsed);

        return AssetProcessingJobOutcome.CommittedSucceeded();
    }

    private async Task<AssetProcessingJobOutcome> CompleteNoOp(
        AssetProcessingJobContext<EmbeddingGenerationPayload> context,
        CancellationToken cancellationToken)
    {
        var marked = await finalizer.MarkJobNoOp(context.JobId, context.LeaseToken, cancellationToken);
        return marked
            ? AssetProcessingJobOutcome.CommittedSucceeded()
            : AssetProcessingJobOutcome.Retryable(
                ErrorCodes.LEASE_LOST,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
    }
}
