using System.Diagnostics;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using Pgvector;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class SimilarAssetsReadMeasurementPostgresTests(PostgresFixture fixture)
{
    private const string VALID_HEX_64 = "e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6";
    private const string VALID_DIGEST = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6";
    private const int DIMENSION = 768;
    private const int CANDIDATE_COUNT = 120;
    private const int WARMUP = 3;
    private const int SAMPLES = 21;

    [Fact]
    public async Task Measure_SimilarAssetsRead_OnSyntheticCategory_ShouldRecordMetadataAndSemanticLatency()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Category other = TestData.CreateCategory("Other", "other");
        db.Categories.Add(other);

        Asset source = AddReady(db, author, category, "measure-source", ratingAverage: 4);
        var candidateIds = new List<Guid>(CANDIDATE_COUNT);
        for (var i = 1; i <= CANDIDATE_COUNT; i++)
        {
            Asset candidate = AddReady(db, author, category, $"measure-{i:D3}", ratingAverage: i % 5);
            candidateIds.Add(candidate.Id);
        }

        for (var i = 0; i < 8; i++)
        {
            AddReady(db, author, other, $"other-{i}");
        }

        EmbeddingOptions embOptions = new()
        {
            Enabled = true,
            Provider = "Ollama",
            Model = "embeddinggemma:300m-qat-q8_0",
            Revision = "manifest-e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            Digest = VALID_DIGEST,
            Dimension = DIMENSION,
            ContentSchemaVersion = AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION
        };
        var modelKey = EmbeddingModelKey.Compute(embOptions);
        AddEmbedding(db, source.Id, modelKey, embOptions, CreateUnitVector(0));
        for (var i = 0; i < candidateIds.Count; i++)
        {
            AddEmbedding(db, candidateIds[i], modelKey, embOptions, CreateUnitVector(i * 0.01f));
        }

        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);
        var semanticOptions = new SimilarAssetsQueryOptions(
            true,
            modelKey,
            DIMENSION,
            embOptions.ContentSchemaVersion);

        ModeMeasurement metadata = await MeasureMode(
            store,
            source.Id,
            SimilarAssetsQueryOptions.MetadataOnly,
            "metadata");
        ModeMeasurement semantic = await MeasureMode(store, source.Id, semanticOptions, "semantic-local-fixtures");

        metadata.ItemCount.Should().Be(6);
        semantic.ItemCount.Should().Be(6);
        metadata.P50Ms.Should().BeGreaterThanOrEqualTo(0);
        semantic.P50Ms.Should().BeGreaterThanOrEqualTo(0);
    }

    private static async Task<ModeMeasurement> MeasureMode(
        AssetStore store,
        Guid sourceId,
        SimilarAssetsQueryOptions options,
        string mode)
    {
        for (var i = 0; i < WARMUP; i++)
        {
            _ = await store.GetSimilarPublic(sourceId, 6, options);
        }

        var samples = new double[SAMPLES];
        var itemCount = 0;
        var stopwatch = new Stopwatch();
        for (var i = 0; i < SAMPLES; i++)
        {
            stopwatch.Restart();
            SimilarPublicAssetsResult? result = await store.GetSimilarPublic(sourceId, 6, options);
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
            itemCount = result?.Items.Count ?? 0;
        }

        Array.Sort(samples);
        return new ModeMeasurement(
            mode,
            itemCount,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples[^1],
            samples);
    }

    private static double Percentile(double[] sortedAscending, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sortedAscending.Length);
        var index = Math.Clamp(rank - 1, 0, sortedAscending.Length - 1);
        return sortedAscending[index];
    }

    private static Asset AddReady(
        ApplicationDbContext db,
        User author,
        Category category,
        string title,
        double ratingAverage = 0)
    {
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title);
        asset.RatingAverage = ratingAverage;
        db.Assets.Add(asset);
        db.AssetVersions.Add(TestData.CreateAssetVersion(
            asset.Id,
            isCurrent: true,
            processingStatus: AssetVersionProcessingStatus.READY));
        return asset;
    }

    private static void AddEmbedding(
        ApplicationDbContext db,
        Guid assetId,
        string modelKey,
        EmbeddingOptions options,
        float[] vector)
    {
        db.AssetEmbeddings.Add(new AssetEmbedding
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            ModelKey = modelKey,
            Provider = options.Provider,
            ModelId = options.Model,
            ModelRevision = options.Revision,
            ModelDigest = options.Digest,
            Dimension = DIMENSION,
            ContentSchemaVersion = options.ContentSchemaVersion,
            SourceRevision = 1,
            ContentHash = VALID_HEX_64,
            Embedding = new Vector(vector),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private static float[] CreateUnitVector(float angle)
    {
        var vector = new float[DIMENSION];
        vector[0] = MathF.Cos(angle);
        vector[1] = MathF.Sin(angle);
        return vector;
    }

    private sealed record ModeMeasurement(
        string Mode,
        int ItemCount,
        double P50Ms,
        double P95Ms,
        double MaxMs,
        double[] SamplesMs);
}
