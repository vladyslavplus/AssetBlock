using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.GetAssets;

internal sealed class GetAssetsQueryHandler(
    IAssetStore assetStore,
    ITypedCache cache,
    IQueryVectorCache? queryVectorCache = null,
    IVectorSearchCapability? vectorCapability = null,
    ITextEmbeddingGenerator? embeddingGenerator = null,
    IOptions<EmbeddingOptions>? embeddingOptions = null,
    ILogger<GetAssetsQueryHandler>? logger = null)
    : IRequestHandler<GetAssetsQuery, Result<CatalogPageResult<AssetListItem>>>
{
    private static readonly TimeSpan _cacheExpiration = CatalogCacheConstants.AssetsListTtl;
    private static readonly TimeSpan _hybridCacheExpiration = CatalogCacheConstants.HybridAssetsListTtl;
    private static readonly TimeSpan _lexicalFallbackCacheExpiration = CatalogCacheConstants.LexicalFallbackListTtl;
    private static readonly TimeSpan _queryVectorCacheExpiration = CatalogCacheConstants.QueryVectorTtl;

    public async Task<Result<CatalogPageResult<AssetListItem>>> Handle(GetAssetsQuery request, CancellationToken cancellationToken)
    {
        GetAssetsRequest normalizedRequest = request.Request with
        {
            Search = CatalogSearchNormalization.NormalizeSearchQuery(request.Request.Search),
            Tags = AssetListNormalization.NormalizeTags(request.Request.Tags)
        };

        var hasExplicitSort = !string.IsNullOrWhiteSpace(normalizedRequest.SortBy)
            && GetAssetsRequest.AllowedSortBy.Contains(normalizedRequest.SortBy);
        var isSearchEmpty = string.IsNullOrWhiteSpace(normalizedRequest.Search);

        // 1. Explicit sort or empty search: 100% lexical path, exact totalCount, deep paging, zero embedding calls.
        if (hasExplicitSort || isSearchEmpty)
        {
            return await HandleLexicalCatalog(normalizedRequest, cancellationToken);
        }

        // 2. Non-empty search with relevance sort (no sortBy): hybrid orchestration
        return await HandleHybridCatalog(normalizedRequest, cancellationToken);
    }

    private async Task<Result<CatalogPageResult<AssetListItem>>> HandleLexicalCatalog(
        GetAssetsRequest request,
        CancellationToken cancellationToken)
    {
        var key = CacheKeys.AssetsList(request);
        CatalogPageResult<AssetListItem>? cached = await cache.Get<CatalogPageResult<AssetListItem>>(key, cancellationToken);
        if (cached is not null)
        {
            logger?.LogDebug("Asset list cache hit for mode {Mode}", "lexical");
            return Result.Success(AssetListNormalization.NormalizeDescriptions(cached));
        }

        CatalogPageResult<AssetListItem> paged = await assetStore.GetPaged(request, null, null, cancellationToken);
        CatalogPageResult<AssetListItem> normalized = AssetListNormalization.NormalizeDescriptions(paged);

        await cache.Set(key, normalized, _cacheExpiration, cancellationToken);
        return Result.Success(normalized);
    }

    private async Task<Result<CatalogPageResult<AssetListItem>>> HandleHybridCatalog(
        GetAssetsRequest request,
        CancellationToken cancellationToken)
    {
        var searchHash = CacheKeys.HashSearchQuery(request.Search);

        // Check vector search capability
        var capability = VectorSearchCapabilityResult.Disabled();
        if (vectorCapability is not null)
        {
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
                logger?.LogWarning(ex, "Failed vector search capability check; falling back to lexical search.");
            }
        }

        if (!capability.IsAvailable || string.IsNullOrWhiteSpace(capability.ModelKey) || embeddingGenerator is null)
        {
            return await HandleLexicalFallback(request, cancellationToken);
        }

        var modelKey = capability.ModelKey;
        var hybridKey = CacheKeys.AssetsListHybrid(request, modelKey);

        // Check cached hybrid result (2 min)
        CatalogPageResult<AssetListItem>? cachedHybrid = await cache.Get<CatalogPageResult<AssetListItem>>(hybridKey, cancellationToken);
        if (cachedHybrid is not null)
        {
            logger?.LogDebug("Asset list cache hit for mode {Mode}", "hybrid");
            return Result.Success(AssetListNormalization.NormalizeDescriptions(cachedHybrid));
        }

        // Resolve query vector (cached 15 min or generate with linked timeout)
        var queryEmbedding = await ResolveQueryEmbedding(request.Search!, modelKey, searchHash, cancellationToken);

        if (queryEmbedding is null)
        {
            return await HandleLexicalFallback(request, cancellationToken);
        }

        // Execute hybrid retrieval
        CatalogPageResult<AssetListItem> paged;
        try
        {
            paged = await assetStore.GetPaged(request, queryEmbedding, modelKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Hybrid retrieval failed in store; falling back to lexical search.");
            return await HandleLexicalFallback(request, cancellationToken);
        }

        CatalogPageResult<AssetListItem> normalized = AssetListNormalization.NormalizeDescriptions(paged);
        await cache.Set(hybridKey, normalized, _hybridCacheExpiration, cancellationToken);
        return Result.Success(normalized);
    }

    private async Task<float[]?> ResolveQueryEmbedding(
        string searchQuery,
        string modelKey,
        string searchHash,
        CancellationToken cancellationToken)
    {
        if (queryVectorCache is not null)
        {
            var cachedVector = queryVectorCache.Get(modelKey, searchHash);
            if (cachedVector is not null)
            {
                var expectedDimension = embeddingOptions?.Value.Dimension ?? 768;
                try
                {
                    VectorValidation.Validate(cachedVector, expectedDimension);
                    return cachedVector;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Cached query vector failed validation; regenerating.");
                }
            }
        }

        var queryTimeoutMs = embeddingOptions?.Value.QueryTimeoutMilliseconds ?? 900;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromMilliseconds(queryTimeoutMs));

        try
        {
            ModelVerificationResult availability = await embeddingGenerator!.CheckModelAvailability(linkedCts.Token);
            if (availability is not { IsAvailable: true })
            {
                logger?.LogWarning(
                    "Embedding model availability check failed ({Reason}); falling back to lexical search.",
                    availability.FailureReason ?? "unavailable");
                return null;
            }

            GeneratedEmbedding generated = await embeddingGenerator.Generate(searchQuery, linkedCts.Token);
            if (!string.Equals(generated.ModelKey, modelKey, StringComparison.Ordinal))
            {
                logger?.LogWarning("Generated embedding model key does not match expected model key; falling back to lexical search.");
                return null;
            }

            var expectedDimension = embeddingOptions?.Value.Dimension ?? 768;
            VectorValidation.Validate(generated.Vector, expectedDimension);

            queryVectorCache?.Set(modelKey, searchHash, generated.Vector, _queryVectorCacheExpiration);
            return generated.Vector;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("Query embedding generation timed out after {TimeoutMs}ms; falling back to lexical search.", queryTimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Query embedding generation failed; falling back to lexical search.");
            return null;
        }
    }

    private async Task<Result<CatalogPageResult<AssetListItem>>> HandleLexicalFallback(
        GetAssetsRequest request,
        CancellationToken cancellationToken)
    {
        var fallbackKey = CacheKeys.AssetsListLexicalFallback(request);

        CatalogPageResult<AssetListItem>? cached = await cache.Get<CatalogPageResult<AssetListItem>>(fallbackKey, cancellationToken);
        if (cached is not null)
        {
            logger?.LogDebug("Asset list cache hit for mode {Mode}", "lexical-fallback");
            return Result.Success(AssetListNormalization.NormalizeDescriptions(cached));
        }

        CatalogPageResult<AssetListItem> paged = await assetStore.GetPaged(request, null, null, cancellationToken);
        CatalogPageResult<AssetListItem> normalized = AssetListNormalization.NormalizeDescriptions(paged);

        await cache.Set(fallbackKey, normalized, _lexicalFallbackCacheExpiration, cancellationToken);
        return Result.Success(normalized);
    }
}
