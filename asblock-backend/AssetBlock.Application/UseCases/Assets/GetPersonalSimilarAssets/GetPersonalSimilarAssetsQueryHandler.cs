using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.GetPersonalSimilarAssets;

internal sealed class GetPersonalSimilarAssetsQueryHandler(
    IAssetStore assetStore,
    IRecommendationExposureSigner exposureSigner,
    TimeProvider? timeProvider = null,
    IVectorSearchCapability? vectorCapability = null,
    IOptions<EmbeddingOptions>? embeddingOptions = null,
    ILogger<GetPersonalSimilarAssetsQueryHandler>? logger = null)
    : IRequestHandler<GetPersonalSimilarAssetsQuery, Result<SimilarAssetsResult>>
{
    public async Task<Result<SimilarAssetsResult>> Handle(
        GetPersonalSimilarAssetsQuery request,
        CancellationToken cancellationToken)
    {
        var usePopularityRanking = request.Mode == SimilarAssetsConstants.MODE_POPULARITY;
        SimilarAssetsQueryOptions rankingOptions = await ResolveRankingOptions(request.UserId, usePopularityRanking, cancellationToken);
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
            request.UserId,
            request.AssetId,
            normalized,
            ranked.UsedSemanticRefinement,
            ranked.UsedPersonalization,
            usePopularityRanking);

        return Result.Success(new SimilarAssetsResult(
            normalized,
            exposure,
            BuildExplanations(normalized, ranked)));
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
        Guid userId,
        Guid sourceAssetId,
        IReadOnlyList<AssetListItem> items,
        bool usedSemanticRefinement,
        bool usedPersonalization,
        bool usePopularityRanking)
    {
        if (items.Count == 0)
        {
            return null;
        }

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        DateTimeOffset expiresAt = now.AddMinutes(RecommendationTelemetryConstants.EXPOSURE_TTL_MINUTES);
        var rankingVersion = usedPersonalization
            ? RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_PERSONAL
            : usePopularityRanking
                ? RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_POPULARITY
                : usedSemanticRefinement
                    ? RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_SEMANTIC
                    : RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA;
        var exposureId = Guid.NewGuid();
        // Audience-bound only for the personal version: Phase A fallback tokens stay
        // byte-identical to public-endpoint tokens regardless of which route issued them.
        var payload = new RecommendationExposurePayload(
            exposureId,
            sourceAssetId,
            rankingVersion,
            expiresAt,
            items.Select(i => i.Id).ToList(),
            usedPersonalization ? userId : null);

        var token = exposureSigner.TryCreateToken(payload);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return new SimilarAssetsExposure(exposureId, rankingVersion, expiresAt, token);
    }

    private async Task<SimilarAssetsQueryOptions> ResolveRankingOptions(
        Guid userId,
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
                UsePopularityRanking: true,
                PersonalUserId: userId);
        }

        EmbeddingOptions? options = embeddingOptions?.Value;
        if (options is not { Enabled: true } || vectorCapability is null)
        {
            return new SimilarAssetsQueryOptions(
                AllowSemanticRefinement: false,
                CanonicalModelKey: null,
                ExpectedDimension: 0,
                ContentSchemaVersion: null,
                UsePopularityRanking: false,
                PersonalUserId: userId);
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
            return new SimilarAssetsQueryOptions(
                AllowSemanticRefinement: false,
                CanonicalModelKey: null,
                ExpectedDimension: 0,
                ContentSchemaVersion: null,
                UsePopularityRanking: false,
                PersonalUserId: userId);
        }

        if (!capability.IsAvailable || string.IsNullOrWhiteSpace(capability.ModelKey))
        {
            return new SimilarAssetsQueryOptions(
                AllowSemanticRefinement: false,
                CanonicalModelKey: null,
                ExpectedDimension: 0,
                ContentSchemaVersion: null,
                UsePopularityRanking: false,
                PersonalUserId: userId);
        }

        return new SimilarAssetsQueryOptions(
            AllowSemanticRefinement: true,
            CanonicalModelKey: capability.ModelKey,
            ExpectedDimension: options.Dimension,
            ContentSchemaVersion: options.ContentSchemaVersion,
            UsePopularityRanking: false,
            PersonalUserId: userId);
    }
}
