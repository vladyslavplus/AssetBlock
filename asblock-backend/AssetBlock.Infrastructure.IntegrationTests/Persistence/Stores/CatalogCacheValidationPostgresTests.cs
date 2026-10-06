using AssetBlock.Application.Common;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pgvector;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class CatalogCacheValidationPostgresTests(PostgresFixture fixture)
{
    private const string MODEL_NAME = "embeddinggemma:300m-qat-q8_0";

    private static readonly EmbeddingOptions _embeddingOptions = new()
    {
        Enabled = true,
        Provider = "Ollama",
        Model = MODEL_NAME,
        Revision = "manifest-e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
        Digest = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
        Dimension = 768,
        ContentSchemaVersion = AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION,
        QueryTimeoutMilliseconds = 900
    };

    private static readonly string _modelKey = EmbeddingModelKey.Compute(_embeddingOptions);

    [Fact]
    public async Task GetAssets_WhenWarmLexicalCacheAndAssetDelisted_ShouldRevalidateAndExcludeAsset()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        (Asset asset, _, _) = await ApprovedPublicationTestBuilder.SeedApprovedListing(
            db,
            author,
            category,
            "Cached Delist Candidate");
        var store = new AssetStore(db);
        ITypedCache cache = Substitute.For<ITypedCache>();

        var lexicalRequest = new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Cached Delist",
            SortBy = "Title",
            SortDirection = SortDirection.ASC
        };
        GetAssetsRequest normalizedLexical = NormalizeRequest(lexicalRequest);
        var lexicalKey = CacheKeys.AssetsList(normalizedLexical);

        CatalogPageResult<AssetListItem> warmPage = await store.GetPaged(normalizedLexical);
        warmPage.Items.Should().ContainSingle(i => i.Id == asset.Id);

        cache.Get<CatalogPageResult<AssetListItem>>(lexicalKey, Arg.Any<CancellationToken>())
            .Returns(warmPage);

        var handler = new GetAssetsQueryHandler(store, cache, logger: NullLogger<GetAssetsQueryHandler>.Instance);

        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        Ardalis.Result.Result<CatalogPageResult<AssetListItem>> result = await handler.Handle(
            new GetAssetsQuery(lexicalRequest),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().NotContain(i => i.Id == asset.Id);
        await cache.Received(1).Get<CatalogPageResult<AssetListItem>>(lexicalKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAssets_WhenWarmLexicalFallbackCacheAndAssetDelisted_ShouldRevalidateAndExcludeAsset()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        (Asset asset, _, _) = await ApprovedPublicationTestBuilder.SeedApprovedListing(
            db,
            author,
            category,
            "Cached Fallback Candidate");
        var store = new AssetStore(db);
        ITypedCache cache = Substitute.For<ITypedCache>();

        var fallbackRequest = new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Cached Fallback"
        };
        GetAssetsRequest normalizedFallback = NormalizeRequest(fallbackRequest);
        var fallbackKey = CacheKeys.AssetsListLexicalFallback(normalizedFallback);

        CatalogPageResult<AssetListItem> warmPage = await store.GetPaged(normalizedFallback);
        warmPage.Items.Should().ContainSingle(i => i.Id == asset.Id);

        cache.Get<CatalogPageResult<AssetListItem>>(fallbackKey, Arg.Any<CancellationToken>())
            .Returns(warmPage);

        var handler = new GetAssetsQueryHandler(store, cache, logger: NullLogger<GetAssetsQueryHandler>.Instance);

        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        Ardalis.Result.Result<CatalogPageResult<AssetListItem>> result = await handler.Handle(
            new GetAssetsQuery(fallbackRequest),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().NotContain(i => i.Id == asset.Id);
        await cache.Received(1).Get<CatalogPageResult<AssetListItem>>(fallbackKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAssets_WhenWarmHybridCacheAndAssetDelisted_ShouldRevalidateViaHybridPathAndExcludeAsset()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        (Asset asset, AssetVersion version, _) = await ApprovedPublicationTestBuilder.SeedApprovedListing(
            db,
            author,
            category,
            "Hybrid Cache Candidate");
        await SeedEmbeddingAsync(db, asset.Id, version.ContentSha256);

        var store = new AssetStore(db);
        ITypedCache cache = Substitute.For<ITypedCache>();
        IVectorSearchCapability vectorCapability = Substitute.For<IVectorSearchCapability>();
        vectorCapability.CheckCapability(Arg.Any<CancellationToken>())
            .Returns(VectorSearchCapabilityResult.Available(_modelKey));

        var queryVector = new float[768];
        queryVector[0] = 1f;
        ITextEmbeddingGenerator embeddingGenerator = Substitute.For<ITextEmbeddingGenerator>();
        embeddingGenerator.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true, null, _embeddingOptions.Digest));
        embeddingGenerator.Generate(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbedding(queryVector, _modelKey, 768));

        var hybridRequest = new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Hybrid Cache"
        };
        GetAssetsRequest normalizedHybrid = NormalizeRequest(hybridRequest);
        var hybridKey = CacheKeys.AssetsListHybrid(normalizedHybrid, _modelKey);

        CatalogPageResult<AssetListItem> warmPage = await store.GetPaged(normalizedHybrid, queryVector, _modelKey);
        warmPage.Items.Should().ContainSingle(i => i.Id == asset.Id);

        cache.Get<CatalogPageResult<AssetListItem>>(hybridKey, Arg.Any<CancellationToken>())
            .Returns(warmPage);

        var handler = new GetAssetsQueryHandler(
            store,
            cache,
            queryVectorCache: null,
            vectorCapability,
            embeddingGenerator,
            Microsoft.Extensions.Options.Options.Create(_embeddingOptions),
            NullLogger<GetAssetsQueryHandler>.Instance);

        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        Ardalis.Result.Result<CatalogPageResult<AssetListItem>> result = await handler.Handle(
            new GetAssetsQuery(hybridRequest),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().NotContain(i => i.Id == asset.Id);
        await cache.Received(1).Get<CatalogPageResult<AssetListItem>>(hybridKey, Arg.Any<CancellationToken>());
        await embeddingGenerator.Received(1).Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static GetAssetsRequest NormalizeRequest(GetAssetsRequest request) =>
        request with
        {
            Search = CatalogSearchNormalization.NormalizeSearchQuery(request.Search),
            Tags = AssetListNormalization.NormalizeTags(request.Tags)
        };

    private static async Task SeedEmbeddingAsync(ApplicationDbContext db, Guid assetId, string contentSha256)
    {
        var vector = new float[768];
        vector[0] = 1f;
        db.AssetEmbeddings.Add(new AssetEmbedding
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            ModelKey = _modelKey,
            Provider = _embeddingOptions.Provider,
            ModelId = _embeddingOptions.Model,
            ModelRevision = _embeddingOptions.Revision,
            ModelDigest = _embeddingOptions.Digest,
            Dimension = _embeddingOptions.Dimension,
            ContentSchemaVersion = _embeddingOptions.ContentSchemaVersion,
            SourceRevision = 1,
            ContentHash = contentSha256,
            Embedding = new Vector(vector),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
