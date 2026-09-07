using System.Globalization;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class EmbeddingBackfillCoordinatorTests
{
    private readonly EmbeddingOptions _defaultOptions = new()
    {
        Enabled = true,
        Provider = "Ollama",
        BaseUrl = "http://127.0.0.1:11434",
        Model = "embeddinggemma:300m-qat-q8_0",
        Revision = "manifest-test",
        Digest = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
        Dimension = 768,
        ContentSchemaVersion = "asset-public-metadata-v1"
    };

    private static ApplicationDbContext CreateInMemoryDbContext()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task GetCursorOffset_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        await using ApplicationDbContext db = CreateInMemoryDbContext();
        IAssetProcessingJobStore jobStore = Substitute.For<IAssetProcessingJobStore>();
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        ICacheService cache = Substitute.For<ICacheService>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        cache.GetString(Arg.Any<string>(), cts.Token)
            .Returns<string?>(_ => throw new OperationCanceledException(cts.Token));

        var sut = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance,
            cacheService: cache);

        Func<Task> act = async () => await sut.GetCursorOffset("model_key", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SetCursorOffset_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        await using ApplicationDbContext db = CreateInMemoryDbContext();
        IAssetProcessingJobStore jobStore = Substitute.For<IAssetProcessingJobStore>();
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        ICacheService cache = Substitute.For<ICacheService>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        cache.SetString(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), cts.Token)
            .Returns<Task>(_ => throw new OperationCanceledException(cts.Token));

        var sut = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance,
            cacheService: cache);

        Func<Task> act = async () => await sut.SetCursorOffset("model_key", 100, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetCursorOffset_WhenCacheThrowsNonCancellationException_LogsWarningAndUsesFallback()
    {
        await using ApplicationDbContext db = CreateInMemoryDbContext();
        IAssetProcessingJobStore jobStore = Substitute.For<IAssetProcessingJobStore>();
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        ICacheService cache = Substitute.For<ICacheService>();

        cache.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new InvalidOperationException("Redis unavailable"));

        var sut = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance,
            cacheService: cache);

        var offset = await sut.GetCursorOffset("model_key", CancellationToken.None);

        offset.Should().Be(0);
    }

    [Fact]
    public async Task SetCursorOffset_WhenCacheThrowsNonCancellationException_UpdatesFallbackWithoutThrowing()
    {
        await using ApplicationDbContext db = CreateInMemoryDbContext();
        IAssetProcessingJobStore jobStore = Substitute.For<IAssetProcessingJobStore>();
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        ICacheService cache = Substitute.For<ICacheService>();

        cache.SetString(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Redis write failure"));

        var sut = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance,
            cacheService: cache);

        // Does not throw
        await sut.SetCursorOffset("test_model_fallback", 42, CancellationToken.None);

        // Fallback offset is preserved
        var retrieved = await sut.GetCursorOffset("test_model_fallback", CancellationToken.None);
        retrieved.Should().Be(42);
    }
}
