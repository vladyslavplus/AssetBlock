using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private const int CONCURRENCY = 1;

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

        var fingerprintInput = string.Join(
            ",",
            new[] { source.Id }.Concat(candidateIds).OrderBy(id => id).Select(id => id.ToString("N")));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant();

        var report = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            corpusEligibleSameCategory = CANDIDATE_COUNT + 1,
            categoryCandidateCount = CANDIDATE_COUNT,
            otherCategoryCount = 8,
            embeddingCoverage = 1.0,
            shortlistBound = 100,
            responseLimit = 6,
            warmup = WARMUP,
            samples = SAMPLES,
            concurrency = CONCURRENCY,
            percentileMethod = "nearest-rank ceiling, 1-based rank = ceil(p * n)",
            sourceFingerprintSha256 = fingerprint,
            gitCommit = TryGitHead(),
            modes = new[] { metadata, semantic },
            sla = "none-numeric-p3-latency-sla-not-defined",
            qualityVerdict = "not-evaluated"
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var planDir = FindPlanDirectory();
        planDir.Should().NotBeNull("measurement artifact should be written next to the P3 plan");
        var path = Path.Combine(planDir, "p3_batch1_similar_assets_read_measurement.json");
        await File.WriteAllTextAsync(path, json);

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

    private static string? FindPlanDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".cursor", "plans");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? TryGitHead()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var gitHead = Path.Combine(dir.FullName, ".git", "HEAD");
                if (File.Exists(gitHead))
                {
                    var head = File.ReadAllText(gitHead).Trim();
                    if (head.StartsWith("ref:", StringComparison.Ordinal))
                    {
                        var refPath = Path.Combine(dir.FullName, ".git", head[5..].Trim().Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(refPath))
                        {
                            return File.ReadAllText(refPath).Trim();
                        }
                    }

                    return head;
                }

                dir = dir.Parent;
            }
        }
        catch (IOException)
        {
        }

        return null;
    }

    private sealed record ModeMeasurement(
        string Mode,
        int ItemCount,
        double P50Ms,
        double P95Ms,
        double MaxMs,
        double[] SamplesMs);
}
