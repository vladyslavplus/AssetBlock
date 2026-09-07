using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class GetAssetsQueryHandlerTests
{
    private const string MODEL_KEY = "embeddinggemma:300m-qat-q8_0";

    private readonly IAssetStore _assetStoreMock;
    private readonly ITypedCache _cacheMock;
    private readonly ITextEmbeddingGenerator _embeddingGeneratorMock;
    private readonly IVectorSearchCapability _vectorCapabilityMock;
    private readonly IQueryVectorCache _queryVectorCacheMock;
    private readonly TestLogger _testLogger = new();
    private readonly GetAssetsQueryHandler _handler;

    private sealed class TestLogger : ILogger<GetAssetsQueryHandler>
    {
        public List<string> CapturedLogs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state != null)
            {
                CapturedLogs.Add(state.ToString() ?? string.Empty);
            }
        }
    }

    public GetAssetsQueryHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _cacheMock = Substitute.For<ITypedCache>();
        _embeddingGeneratorMock = Substitute.For<ITextEmbeddingGenerator>();
        _vectorCapabilityMock = Substitute.For<IVectorSearchCapability>();
        _queryVectorCacheMock = Substitute.For<IQueryVectorCache>();
        _embeddingGeneratorMock.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true));

        var embeddingOptions = new EmbeddingOptions
        {
            Model = "embeddinggemma:300m-qat-q8_0",
            Dimension = 768,
            QueryTimeoutMilliseconds = 900
        };

        _handler = new GetAssetsQueryHandler(
            _assetStoreMock,
            _cacheMock,
            _queryVectorCacheMock,
            _vectorCapabilityMock,
            _embeddingGeneratorMock,
            Microsoft.Extensions.Options.Options.Create(embeddingOptions),
            _testLogger);
    }

    [Fact]
    public async Task Handle_WhenEmptySearch_ShouldNeverCallEmbeddingGenerator()
    {
        _cacheMock.Get<CatalogPageResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CatalogPageResult<AssetListItem>?)null);
        var emptyPaged = new CatalogPageResult<AssetListItem>([], 0, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), null, null, Arg.Any<CancellationToken>())
            .Returns(emptyPaged);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10, Search = "   " };
        var query = new GetAssetsQuery(request);

        Result<CatalogPageResult<AssetListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _embeddingGeneratorMock.DidNotReceiveWithAnyArgs().Generate(null!);
        await _assetStoreMock.Received(1).GetPaged(
            Arg.Is<GetAssetsRequest>(r => r.Search == null),
            null,
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenExplicitSort_ShouldNeverCallEmbeddingGenerator()
    {
        _cacheMock.Get<CatalogPageResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CatalogPageResult<AssetListItem>?)null);
        var emptyPaged = new CatalogPageResult<AssetListItem>([], 0, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), null, null, Arg.Any<CancellationToken>())
            .Returns(emptyPaged);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10, Search = "sword", SortBy = "price" };
        var query = new GetAssetsQuery(request);

        Result<CatalogPageResult<AssetListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _embeddingGeneratorMock.DidNotReceiveWithAnyArgs().Generate(null!);
        await _assetStoreMock.Received(1).GetPaged(
            Arg.Is<GetAssetsRequest>(r => r.Search == "sword" && r.SortBy == "price"),
            null,
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenRelevanceSearchAndVectorAvailable_ShouldCallGeneratorAndStoreInLocalVectorCache()
    {
        _cacheMock.Get<CatalogPageResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CatalogPageResult<AssetListItem>?)null);

        _vectorCapabilityMock.CheckCapability(Arg.Any<CancellationToken>())
            .Returns(VectorSearchCapabilityResult.Available(MODEL_KEY));

        var vector = new float[768];
        vector[0] = 0.5f;
        _embeddingGeneratorMock.Generate("sci-fi armor", Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbedding(vector, MODEL_KEY, 768));

        var hybridAssets = new List<AssetListItem>
        {
            new(
                Guid.NewGuid(),
                "Sci-Fi Armor",
                null,
                19.99m,
                Guid.NewGuid(),
                "3D",
                Guid.NewGuid(),
                "author",
                DateTimeOffset.UtcNow,
                [],
                0)
        };
        var pagedResult = new CatalogPageResult<AssetListItem>(hybridAssets, 1, 1, 10);
        _assetStoreMock.GetPaged(
            Arg.Any<GetAssetsRequest>(),
            Arg.Any<float[]>(),
            MODEL_KEY,
            Arg.Any<CancellationToken>())
            .Returns(pagedResult);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10, Search = "sci-fi armor" };
        var query = new GetAssetsQuery(request);

        Result<CatalogPageResult<AssetListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(a => a.Title == "Sci-Fi Armor");
        await _embeddingGeneratorMock.Received(1).Generate("sci-fi armor", Arg.Any<CancellationToken>());

        // Process-local query vector cache should be populated
        _queryVectorCacheMock.Received(1).Set(
            MODEL_KEY,
            CacheKeys.HashSearchQuery("sci-fi armor"),
            Arg.Is<float[]>(v => v.Length == 768),
            CatalogCacheConstants.QueryVectorTtl);

        // Distributed cache must NEVER store query vectors
        await _cacheMock.DidNotReceive().Set(
            Arg.Is<string>(k => k.Contains(":query-vector:")),
            Arg.Any<object>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());

        // Hybrid result is stored in distributed cache for 2 minutes
        await _cacheMock.Received(1).Set(
            Arg.Is<string>(k => k.Contains(":hybrid:")),
            Arg.Any<CatalogPageResult<AssetListItem>>(),
            CatalogCacheConstants.HybridAssetsListTtl,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenQueryVectorInLocalCache_ShouldUseCachedVectorWithoutCallingGenerator()
    {
        _cacheMock.Get<CatalogPageResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CatalogPageResult<AssetListItem>?)null);

        _vectorCapabilityMock.CheckCapability(Arg.Any<CancellationToken>())
            .Returns(VectorSearchCapabilityResult.Available(MODEL_KEY));

        var cachedVector = new float[768];
        cachedVector[0] = 0.77f;
        var searchHash = CacheKeys.HashSearchQuery("sci-fi armor");
        _queryVectorCacheMock.Get(MODEL_KEY, searchHash)
            .Returns(cachedVector);

        var pagedResult = new CatalogPageResult<AssetListItem>([], 0, 1, 10);
        _assetStoreMock.GetPaged(
            Arg.Any<GetAssetsRequest>(),
            cachedVector,
            MODEL_KEY,
            Arg.Any<CancellationToken>())
            .Returns(pagedResult);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10, Search = "sci-fi armor" };
        var query = new GetAssetsQuery(request);

        Result<CatalogPageResult<AssetListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _embeddingGeneratorMock.DidNotReceiveWithAnyArgs().Generate(null!);
        await _assetStoreMock.Received(1).GetPaged(
            Arg.Any<GetAssetsRequest>(),
            cachedVector,
            MODEL_KEY,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenHybridDbRetrievalFails_DoesNotCacheHybridResult_AndCachesLexicalFallback()
    {
        _cacheMock.Get<CatalogPageResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CatalogPageResult<AssetListItem>?)null);

        _vectorCapabilityMock.CheckCapability(Arg.Any<CancellationToken>())
            .Returns(VectorSearchCapabilityResult.Available(MODEL_KEY));

        var vector = new float[768];
        _embeddingGeneratorMock.Generate("sci-fi armor", Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbedding(vector, MODEL_KEY, 768));

        // Hybrid call throws exception (e.g. Postgres pgvector / connection error)
        _assetStoreMock.GetPaged(
            Arg.Any<GetAssetsRequest>(),
            vector,
            MODEL_KEY,
            Arg.Any<CancellationToken>())
            .Returns<Task<CatalogPageResult<AssetListItem>>>(_ => throw new InvalidOperationException("DB connection error"));

        // Fallback lexical call succeeds
        var fallbackAssets = new List<AssetListItem>
        {
            new(
                Guid.NewGuid(),
                "Lexical Armor",
                null,
                15m,
                Guid.NewGuid(),
                "3D",
                Guid.NewGuid(),
                "author",
                DateTimeOffset.UtcNow,
                [],
                0)
        };
        var fallbackPaged = new CatalogPageResult<AssetListItem>(fallbackAssets, 1, 1, 10);
        _assetStoreMock.GetPaged(
            Arg.Any<GetAssetsRequest>(),
            null,
            null,
            Arg.Any<CancellationToken>())
            .Returns(fallbackPaged);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10, Search = "sci-fi armor" };
        var query = new GetAssetsQuery(request);

        Result<CatalogPageResult<AssetListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(a => a.Title == "Lexical Armor");

        // CRITICAL: Hybrid key (2-minute TTL) must NEVER be cached on hybrid DB failure
        await _cacheMock.DidNotReceive().Set(
            Arg.Is<string>(k => k.Contains(":hybrid:")),
            Arg.Any<CatalogPageResult<AssetListItem>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());

        // CRITICAL: Lexical fallback key (15-second TTL) MUST be cached
        await _cacheMock.Received(1).Set(
            Arg.Is<string>(k => k.Contains(":lexical-fallback:")),
            Arg.Is<CatalogPageResult<AssetListItem>>(r => r.Items.Count == 1),
            CatalogCacheConstants.LexicalFallbackListTtl,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenVectorCapabilityUnavailable_ShouldFallBackToLexical()
    {
        _cacheMock.Get<CatalogPageResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CatalogPageResult<AssetListItem>?)null);

        _vectorCapabilityMock.CheckCapability(Arg.Any<CancellationToken>())
            .Returns(VectorSearchCapabilityResult.Disabled());

        var lexicalAssets = new List<AssetListItem>
        {
            new(
                Guid.NewGuid(),
                "Lexical Item",
                null,
                5m,
                Guid.NewGuid(),
                "Cat",
                Guid.NewGuid(),
                "u",
                DateTimeOffset.UtcNow,
                [],
                0)
        };
        var pagedResult = new CatalogPageResult<AssetListItem>(lexicalAssets, 1, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), null, null, Arg.Any<CancellationToken>())
            .Returns(pagedResult);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10, Search = "fantasy castle" };
        var query = new GetAssetsQuery(request);

        Result<CatalogPageResult<AssetListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(a => a.Title == "Lexical Item");
        await _embeddingGeneratorMock.DidNotReceiveWithAnyArgs().Generate(null!);
        await _cacheMock.Received(1).Set(
            Arg.Is<string>(k => k.Contains(":lexical-fallback:")),
            Arg.Any<CatalogPageResult<AssetListItem>>(),
            CatalogCacheConstants.LexicalFallbackListTtl,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenEmbeddingGenerationTimesOut_ShouldFallBackToLexical()
    {
        _cacheMock.Get<CatalogPageResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CatalogPageResult<AssetListItem>?)null);

        _vectorCapabilityMock.CheckCapability(Arg.Any<CancellationToken>())
            .Returns(VectorSearchCapabilityResult.Available(MODEL_KEY));

        _embeddingGeneratorMock.Generate("slow query", Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbedding>>(_ => throw new OperationCanceledException());

        var fallbackPaged = new CatalogPageResult<AssetListItem>([], 0, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), null, null, Arg.Any<CancellationToken>())
            .Returns(fallbackPaged);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10, Search = "slow query" };
        var query = new GetAssetsQuery(request);

        Result<CatalogPageResult<AssetListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _assetStoreMock.Received(1).GetPaged(
            Arg.Is<GetAssetsRequest>(r => r.Search == "slow query"),
            null,
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PrivacyGuardrails_CacheKeysAndLogsNeverContainRawQueryOrSentinelTag()
    {
        const string rawQuery = "secret-nuclear-submarine";
        const string sentinelTag = "classified-sentinel-tag";
        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        _cacheMock.Get<CatalogPageResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CatalogPageResult<AssetListItem>?)null);

        _vectorCapabilityMock.CheckCapability(Arg.Any<CancellationToken>())
            .Returns(VectorSearchCapabilityResult.Disabled());

        var paged = new CatalogPageResult<AssetListItem>([], 0, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), null, null, Arg.Any<CancellationToken>())
            .Returns(paged);

        var request = new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = rawQuery,
            Tags = [sentinelTag],
            AuthorId = authorId,
            CategoryId = categoryId
        };
        var query = new GetAssetsQuery(request);

        await _handler.Handle(query, CancellationToken.None);

        var expectedSearchHash = CacheKeys.HashSearchQuery(rawQuery);
        var expectedFilterHash = CacheKeys.HashFilterComponent(request);

        // Verify cache keys written
        await _cacheMock.Received().Set(
            Arg.Is<string>(key =>
                !key.Contains(rawQuery) &&
                !key.Contains(sentinelTag) &&
                !key.Contains(authorId.ToString()) &&
                !key.Contains(categoryId.ToString()) &&
                key.Contains(expectedSearchHash) &&
                key.Contains(expectedFilterHash)),
            Arg.Any<CatalogPageResult<AssetListItem>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());

        // Verify captured logs do NOT contain any sensitive query or filter content
        foreach (var logMessage in _testLogger.CapturedLogs)
        {
            logMessage.Should().NotContain(rawQuery);
            logMessage.Should().NotContain(sentinelTag);
            logMessage.Should().NotContain(authorId.ToString());
            logMessage.Should().NotContain(categoryId.ToString());
        }
    }

    [Fact]
    public async Task Handle_WhenEmbeddingModelDigestMismatch_ShouldFallBackToLexical_AndNeverCallHybrid()
    {
        _cacheMock.Get<CatalogPageResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CatalogPageResult<AssetListItem>?)null);

        _vectorCapabilityMock.CheckCapability(Arg.Any<CancellationToken>())
            .Returns(VectorSearchCapabilityResult.Available(MODEL_KEY));

        _embeddingGeneratorMock.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(false, "Model digest mismatch", "sha256:different"));

        var fallbackPaged = new CatalogPageResult<AssetListItem>([], 0, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), null, null, Arg.Any<CancellationToken>())
            .Returns(fallbackPaged);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10, Search = "sci-fi armor" };
        var query = new GetAssetsQuery(request);

        Result<CatalogPageResult<AssetListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _embeddingGeneratorMock.DidNotReceiveWithAnyArgs().Generate(null!);
        await _assetStoreMock.DidNotReceive().GetPaged(
            Arg.Any<GetAssetsRequest>(),
            Arg.Is<float[]>(v => v != null),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        await _cacheMock.Received(1).Set(
            Arg.Is<string>(k => k.Contains(":lexical-fallback:")),
            Arg.Any<CatalogPageResult<AssetListItem>>(),
            CatalogCacheConstants.LexicalFallbackListTtl,
            Arg.Any<CancellationToken>());

        await _cacheMock.DidNotReceive().Set(
            Arg.Is<string>(k => k.Contains(":hybrid:")),
            Arg.Any<CatalogPageResult<AssetListItem>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenGeneratedEmbeddingModelKeyMismatch_ShouldFallBackToLexical_AndNeverCallHybrid()
    {
        _cacheMock.Get<CatalogPageResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CatalogPageResult<AssetListItem>?)null);

        _vectorCapabilityMock.CheckCapability(Arg.Any<CancellationToken>())
            .Returns(VectorSearchCapabilityResult.Available(MODEL_KEY));

        _embeddingGeneratorMock.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true));

        var vector = new float[768];
        _embeddingGeneratorMock.Generate("sci-fi armor", Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbedding(vector, "unexpected-model-key", 768));

        var fallbackPaged = new CatalogPageResult<AssetListItem>([], 0, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), null, null, Arg.Any<CancellationToken>())
            .Returns(fallbackPaged);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10, Search = "sci-fi armor" };
        var query = new GetAssetsQuery(request);

        Result<CatalogPageResult<AssetListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _queryVectorCacheMock.DidNotReceiveWithAnyArgs().Set(null!, null!, null!, TimeSpan.Zero);
        await _assetStoreMock.DidNotReceive().GetPaged(
            Arg.Any<GetAssetsRequest>(),
            Arg.Is<float[]>(v => v != null),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        await _cacheMock.Received(1).Set(
            Arg.Is<string>(k => k.Contains(":lexical-fallback:")),
            Arg.Any<CatalogPageResult<AssetListItem>>(),
            CatalogCacheConstants.LexicalFallbackListTtl,
            Arg.Any<CancellationToken>());

        await _cacheMock.DidNotReceive().Set(
            Arg.Is<string>(k => k.Contains(":hybrid:")),
            Arg.Any<CatalogPageResult<AssetListItem>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }
}
