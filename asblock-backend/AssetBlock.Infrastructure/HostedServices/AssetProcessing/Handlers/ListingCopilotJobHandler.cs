using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.HostedServices.AssetProcessing.Handlers;

internal sealed class ListingCopilotJobHandler(
    IAssetStore assetStore,
    IAssetArchiveAnalysisStore analysisStore,
    IListingCopilotStore listingCopilotStore,
    IListingSuggestionOrchestrator orchestrator,
    ILogger<ListingCopilotJobHandler> logger) : IAssetProcessingJobHandler<ListingCopilotPayload, ListingCopilotResult>
{
    public async Task<AssetProcessingJobOutcome> Process(
        AssetProcessingJobContext<ListingCopilotPayload> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.DefinitionVersion != AiPromptPolicies.LISTING_COPILOT_DEFINITION_VERSION
            || !string.Equals(context.Payload.PolicyVersion, AiPromptPolicies.LISTING_COPILOT_V1, StringComparison.Ordinal))
        {
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.INVALID_JOB_PAYLOAD,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.INVALID_JOB_PAYLOAD));
        }

        var version = await assetStore.GetVersion(context.AssetId, context.AssetVersionId, cancellationToken);
        if (version is null)
        {
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.VERSION_NOT_FOUND,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.VERSION_NOT_FOUND));
        }

        var analysis = await analysisStore.GetByVersionId(context.AssetVersionId, cancellationToken);
        if (analysis is null)
        {
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ERR_AI_ARCHIVE_ANALYSIS_MISSING,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_AI_ARCHIVE_ANALYSIS_MISSING));
        }

        IReadOnlyList<RecognizedManifestItem> manifests;
        try
        {
            manifests = string.IsNullOrWhiteSpace(analysis.ManifestMetadata)
                ? []
                : ArchiveAnalysisSerializer.DeserializeManifestMetadata(analysis.ManifestMetadata).Manifests;
        }
        catch (ArchiveAnalysisSerializerException)
        {
            logger.LogInformation("Listing copilot rejected malformed archive metadata for job {JobId}", context.JobId);
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.INVALID_JOB_PAYLOAD,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.INVALID_JOB_PAYLOAD));
        }

        var categories = await listingCopilotStore.ListCategoryNames(cancellationToken);
        var tags = await listingCopilotStore.ListTagNames(cancellationToken);
        if (categories.Count > ListingSuggestionBounds.MAX_ALLOWLIST_CATEGORIES
            || tags.Count > ListingSuggestionBounds.MAX_ALLOWLIST_TAGS)
        {
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ERR_AI_ALLOWLIST_OVERFLOW,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_AI_ALLOWLIST_OVERFLOW));
        }

        SafeReadmeExcerpt? readme = string.IsNullOrWhiteSpace(analysis.ReadmeContent)
            ? null
            : new SafeReadmeExcerpt(ListingSuggestionBounds.README_LABEL, analysis.ReadmeContent);

        var request = new ListingSuggestionGenerationRequest(
            AiPromptPolicies.LISTING_COPILOT_V1,
            readme,
            new NormalizedArchiveMetadata(
                ArchiveFormatFromFileName(version.FileName),
                analysis.FileCount,
                analysis.TotalExpandedBytes,
                [],
                manifests),
            categories,
            tags);

        var result = await orchestrator.Generate(request, cancellationToken);
        return result.Outcome switch
        {
            AiGenerationOutcomeKind.SUCCESS => await CommitSuccess(context, result, categories, tags, cancellationToken),
            AiGenerationOutcomeKind.RETRYABLE_FAILURE => AssetProcessingJobOutcome.Retryable(
                result.ErrorCode ?? ErrorCodes.ERR_AI_ERROR,
                SafeSummary(result.ErrorCode),
                result.RetryAfter),
            AiGenerationOutcomeKind.DISABLED => AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ERR_AI_DISABLED,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_AI_DISABLED)),
            _ => AssetProcessingJobOutcome.Terminal(
                result.ErrorCode ?? ErrorCodes.ERR_AI_ERROR,
                SafeSummary(result.ErrorCode))
        };
    }

    private async Task<AssetProcessingJobOutcome> CommitSuccess(
        AssetProcessingJobContext<ListingCopilotPayload> context,
        ListingSuggestionResult result,
        IReadOnlyList<string> allowedCategories,
        IReadOnlyList<string> allowedTags,
        CancellationToken cancellationToken)
    {
        if (result.Suggestion is null
            || string.IsNullOrWhiteSpace(result.ActualModel)
            || !allowedCategories.Contains(result.Suggestion.Category, StringComparer.Ordinal)
            || result.Suggestion.Tags.Any(tag => !allowedTags.Contains(tag, StringComparer.Ordinal)))
        {
            return AssetProcessingJobOutcome.Terminal(
                ErrorCodes.ERR_AI_INVALID_RESPONSE,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_AI_INVALID_RESPONSE));
        }

        var hash = ListingSuggestionCanonicalizer.ComputeContentHash(result.Suggestion);
        var committed = await listingCopilotStore.TryCommitSucceeded(
            context.JobId,
            context.LeaseToken,
            context.AssetId,
            context.AssetVersionId,
            new ListingCopilotSuggestionWrite(
                context.JobId,
                AiPromptPolicies.LISTING_COPILOT_V1,
                result.RequestedProvider,
                result.ActualModel,
                result.ModelRevision,
                result.UpstreamProvider,
                result.RequestId,
                result.Suggestion.Title,
                result.Suggestion.Description,
                result.Suggestion.Category,
                result.Suggestion.Tags,
                hash,
                result.InputTokens,
                result.OutputTokens),
            cancellationToken);

        if (!committed)
        {
            return AssetProcessingJobOutcome.Retryable(
                ErrorCodes.LEASE_LOST,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_LOST));
        }

        return AssetProcessingJobOutcome.CommittedSucceeded();
    }

    private static string SafeSummary(string? errorCode) =>
        ErrorCodesToErrorMessages.GetMessage(errorCode ?? ErrorCodes.ERR_AI_ERROR);

    private static string ArchiveFormatFromFileName(string fileName)
    {
        var name = fileName.Trim().ToLowerInvariant();
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal))
        {
            return "tar.gz";
        }

        if (name.EndsWith(".tar", StringComparison.Ordinal))
        {
            return "tar";
        }

        var extension = Path.GetExtension(name);
        return extension.Length > 1 ? extension[1..] : "zip";
    }
}
