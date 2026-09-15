using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.GetSimilarAssets;

internal sealed class GetSimilarAssetsQueryHandler(
    IAssetStore assetStore,
    IRecommendationExposureSigner exposureSigner,
    TimeProvider? timeProvider = null,
    IVectorSearchCapability? vectorCapability = null,
    IOptions<EmbeddingOptions>? embeddingOptions = null,
    ILogger<GetSimilarAssetsQueryHandler>? logger = null)
    : IRequestHandler<GetSimilarAssetsQuery, Result<SimilarAssetsResult>>
{
    public async Task<Result<SimilarAssetsResult>> Handle(
        GetSimilarAssetsQuery request,
        CancellationToken cancellationToken)
    {
        var usePopularityRanking = request.Mode == SimilarAssetsConstants.MODE_POPULARITY;
        SimilarAssetsQueryOptions rankingOptions = await ResolveRankingOptions(usePopularityRanking, cancellationToken);
        SimilarPublicAssetsResult? ranked = await assetStore.GetSimilarPublic(
            request.AssetId,
            request.Limit,
            rankingOptions,
            cancellationToken);

        if (ranked is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        IReadOnlyList<AssetListItem> normalized = ranked.Items
            .Select(i => i with { Description = string.IsNullOrWhiteSpace(i.Description) ? null : i.Description })
            .ToList();

        SimilarAssetsExposure? exposure = TryIssueExposure(
            request.AssetId,
            normalized,
            ranked.UsedSemanticRefinement,
            usePopularityRanking);

        return Result.Success(new SimilarAssetsResult(normalized, exposure, BuildExplanations(normalized, ranked)));
    }

    private static IReadOnlyList<SimilarAssetExplanation> BuildExplanations(
        IReadOnlyList<AssetListItem> items,
        SimilarPublicAssetsResult ranked)
    {
        // Explanations follow items order exactly; missing evidence degrades to the
        // same-category fallback, which always holds by eligibility. Ranking is untouched.
        return items
            .Select(i => SimilarAssetsExplanations.Build(
                i.Id,
                ranked.Evidence?.GetValueOrDefault(i.Id) ?? SimilarAssetsExplanations.Fallback()))
            .ToList();
    }

    private SimilarAssetsExposure? TryIssueExposure(
        Guid sourceAssetId,
        IReadOnlyList<AssetListItem> items,
        bool usedSemanticRefinement,
        bool usePopularityRanking)
    {
        if (items.Count == 0)
        {
            return null;
        }

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        DateTimeOffset expiresAt = now.AddMinutes(RecommendationTelemetryConstants.EXPOSURE_TTL_MINUTES);
        var rankingVersion = usePopularityRanking
            ? RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_POPULARITY
            : usedSemanticRefinement
                ? RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_SEMANTIC
                : RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA;
        var exposureId = Guid.NewGuid();
        var payload = new RecommendationExposurePayload(
            exposureId,
            sourceAssetId,
            rankingVersion,
            expiresAt,
            items.Select(i => i.Id).ToList());

        var token = exposureSigner.TryCreateToken(payload);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return new SimilarAssetsExposure(exposureId, rankingVersion, expiresAt, token);
    }

    private async Task<SimilarAssetsQueryOptions> ResolveRankingOptions(
        bool usePopularityRanking,
        CancellationToken cancellationToken)
    {
        if (usePopularityRanking)
        {
            return new SimilarAssetsQueryOptions(
                AllowSemanticRefinement: false,
                CanonicalModelKey: null,
                ExpectedDimension: 0,
                ContentSchemaVersion: null,
                UsePopularityRanking: true);
        }

        EmbeddingOptions? options = embeddingOptions?.Value;
        if (options is not { Enabled: true } || vectorCapability is null)
        {
            return SimilarAssetsQueryOptions.MetadataOnly;
        }

        VectorSearchCapabilityResult capability;
        try
        {
            capability = await vectorCapability.CheckCapability(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Similar-assets vector capability check failed; using metadata ranking.");
            return SimilarAssetsQueryOptions.MetadataOnly;
        }

        if (!capability.IsAvailable || string.IsNullOrWhiteSpace(capability.ModelKey))
        {
            return SimilarAssetsQueryOptions.MetadataOnly;
        }

        return new SimilarAssetsQueryOptions(
            AllowSemanticRefinement: true,
            CanonicalModelKey: capability.ModelKey,
            ExpectedDimension: options.Dimension,
            ContentSchemaVersion: options.ContentSchemaVersion);
    }
}
