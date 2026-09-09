using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Entities;
using AssetBlock.Infrastructure.Persistence.Interceptors;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.SearchEvaluation.Infrastructure;
using AssetBlock.SearchEvaluation.Profiling;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;

namespace AssetBlock.Application.Tests.SearchEvaluation;

[Collection("SearchSqlProfileIntegrationTests")]
public sealed class SearchSqlProfileIntegrationTests
{
    [Fact]
    public async Task SqlProfiling_WhenExecutedOnIsolatedDb_CapturesActualStoreSqlAndReplaysSanitizedExplainWithoutAnnDdl()
    {
        // Arrange: Start isolated disposable PostgreSQL testcontainer
        await using var fixture = new SearchEvaluationDbFixture();
        await fixture.InitializeAsync();

        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var embeddingOptions = new EmbeddingOptions
        {
            Model = "embeddinggemma:300m-qat-q8_0",
            Revision = "test-rev",
            Digest = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            Dimension = 768
        };
        var modelKey = EmbeddingModelKey.Compute(embeddingOptions);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var queryVector = new float[768];
        queryVector[0] = 1.0f;

        // Seed 1 valid eligible asset and 1 soft-deleted asset (negative fixture)
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            db.Users.Add(new User
            {
                Id = authorId,
                Username = "profile_author",
                Email = "profile_author@example.com",
                PasswordHash = "hash",
                Role = AppRoles.USER,
                CreatedAt = now,
                UpdatedAt = now
            });

            db.Categories.Add(new Category
            {
                Id = categoryId,
                Name = "Weapons",
                Slug = "weapons",
                CreatedAt = now
            });

            // Eligible Asset
            var assetId = Guid.NewGuid();
            db.Assets.Add(new Asset
            {
                Id = assetId,
                AuthorId = authorId,
                CategoryId = categoryId,
                Title = "Medieval Broadsword",
                Description = "Steel broadsword 3D model",
                Price = 25.00m,
                CreatedAt = now,
                UpdatedAt = now,
                SearchRevision = 1L
            });
            db.AssetVersions.Add(new AssetVersion
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                VersionNumber = 1,
                IsCurrent = true,
                StorageKey = "weapons/broadsword.zip",
                FileName = "broadsword.zip",
                ContentSha256 = new string('a', 64),
                ContentLength = 2048,
                ReleaseNotes = "Initial release",
                LicenseCode = AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Standard License",
                LicenseTerms = "Standard terms",
                ProcessingStatus = AssetVersionProcessingStatus.READY,
                ProcessingUpdatedAt = now,
                CreatedAt = now
            });
            db.AssetEmbeddings.Add(new AssetEmbedding
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                ModelKey = modelKey,
                Provider = "LocalOllama",
                ModelId = "embeddinggemma:300m-qat-q8_0",
                ModelRevision = "test-rev",
                ModelDigest = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
                Dimension = 768,
                ContentSchemaVersion = "asset-public-metadata-v1",
                SourceRevision = 1L,
                ContentHash = new string('1', 64),
                Embedding = new Vector(queryVector),
                CreatedAt = now,
                UpdatedAt = now
            });

            // Negative Fixture: Soft-deleted asset
            var deletedAssetId = Guid.NewGuid();
            db.Assets.Add(new Asset
            {
                Id = deletedAssetId,
                AuthorId = authorId,
                CategoryId = categoryId,
                Title = "Deleted Broadsword",
                Description = "Deleted asset",
                Price = 25.00m,
                CreatedAt = now,
                UpdatedAt = now,
                DeletedAt = now,
                SearchRevision = 1L
            });

            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("ANALYZE;");
        }

        // Act 1: Intercept actual AssetStore.GetPaged execution
        var interceptor = new SqlProfilingInterceptor();
        DbContextOptionsBuilder<ApplicationDbContext> optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(new AuditTimestampsInterceptor(TimeProvider.System), interceptor);

        CatalogPageResult<AssetListItem> result;
        await using (var profilingDb = new ApplicationDbContext(optionsBuilder.Options))
        {
            var store = new AssetStore(profilingDb);
            var req = new GetAssetsRequest
            {
                Search = "Broadsword",
                CategoryId = categoryId,
                Page = 1,
                PageSize = 20
            };

            result = await store.GetPaged(req, queryVector, modelKey, CancellationToken.None);
        }

        // Assert 1: Only the active eligible asset was returned
        result.TotalCount.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].Title.Should().Be("Medieval Broadsword");

        // Act 2: Verify captured commands from real store
        IReadOnlyList<CapturedDbCommand> capturedCommands = interceptor.GetCapturedCommands();
        capturedCommands.Should().NotBeEmpty();

        // Act 3: Replay parameterized EXPLAIN outside measured loop on isolated connection
        await using NpgsqlConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();

        foreach (CapturedDbCommand cmd in capturedCommands)
        {
            SanitizedExplainPlan plan = await SqlProfilingInterceptor.ReplayExplainAsync(connection, cmd, CancellationToken.None);

            // Assert 3: EXPLAIN plan was captured, sanitized, and contains structural metrics
            plan.RootNode.NodeType.Should().NotBeNullOrWhiteSpace();
            plan.ExecutionTimeMs.Should().BeGreaterThanOrEqualTo(0.0);
            plan.PlanningTimeMs.Should().BeGreaterThanOrEqualTo(0.0);

            // Verify no raw queries or expressions exist in sanitized model
            var serialized = System.Text.Json.JsonSerializer.Serialize(plan);
            serialized.Should().NotContain("Filter");
            serialized.Should().NotContain("Index Cond");
            serialized.Should().NotContain("broadsword");
        }

        // Assert 4: Verify NO HNSW or ANN index exists in database schema
        await using NpgsqlCommand checkCmd = connection.CreateCommand();
        {
            checkCmd.CommandText = """
                SELECT count(*)
                FROM pg_indexes
                WHERE tablename = 'asset_embeddings' AND indexname LIKE '%hnsw%';
                """;
            var hnswCount = Convert.ToInt64(await checkCmd.ExecuteScalarAsync());
            hnswCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task CountEligibleAssetsAsync_WhenEvaluatedAgainstPostgresFixtures_MatchesProductionPredicatesStrictly()
    {
        // Arrange: Start isolated disposable PostgreSQL testcontainer
        await using var fixture = new SearchEvaluationDbFixture();
        await fixture.InitializeAsync();

        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var embeddingOptions = new EmbeddingOptions
        {
            Model = "embeddinggemma:300m-qat-q8_0",
            Revision = "test-rev",
            Digest = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            Dimension = 768
        };
        var modelKey = EmbeddingModelKey.Compute(embeddingOptions);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var vector = new Vector(new float[768]);

        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            db.Users.Add(new User
            {
                Id = authorId,
                Username = "author_counts",
                Email = "author_counts@example.com",
                PasswordHash = "hash",
                Role = AppRoles.USER,
                CreatedAt = now,
                UpdatedAt = now
            });

            db.Categories.Add(new Category
            {
                Id = categoryId,
                Name = "Weapons",
                Slug = "weapons",
                CreatedAt = now
            });

            // 1. Fully eligible asset: IsCurrent = true, READY, matching ModelKey, SourceRevision == SearchRevision
            var validId = Guid.NewGuid();
            db.Assets.Add(new Asset { Id = validId, AuthorId = authorId, CategoryId = categoryId, Title = "Valid Asset", Description = "Desc", Price = 10m, CreatedAt = now, UpdatedAt = now, SearchRevision = 1L });
            db.AssetVersions.Add(new AssetVersion { Id = Guid.NewGuid(), AssetId = validId, VersionNumber = 1, IsCurrent = true, StorageKey = "k1", FileName = "f1", ContentSha256 = new string('1', 64), ContentLength = 100, ReleaseNotes = "r", LicenseCode = AssetLicenseCode.PERSONAL, LicenseTemplateVersion = "1.0", LicenseDisplayName = "L", LicenseTerms = "T", ProcessingStatus = AssetVersionProcessingStatus.READY, ProcessingUpdatedAt = now, CreatedAt = now });
            db.AssetEmbeddings.Add(new AssetEmbedding { Id = Guid.NewGuid(), AssetId = validId, ModelKey = modelKey, Provider = "Ollama", ModelId = "model", ModelRevision = "test-rev", ModelDigest = embeddingOptions.Digest, Dimension = 768, ContentSchemaVersion = "v1", SourceRevision = 1L, ContentHash = new string('1', 64), Embedding = vector, CreatedAt = now, UpdatedAt = now });

            // 2. Noncurrent READY version asset: old READY version is IsCurrent = false, candidate version is IsCurrent = false, PENDING_INSPECTION (no current ready version)
            var noncurrentId = Guid.NewGuid();
            db.Assets.Add(new Asset { Id = noncurrentId, AuthorId = authorId, CategoryId = categoryId, Title = "Noncurrent Asset", Description = "Desc", Price = 10m, CreatedAt = now, UpdatedAt = now, SearchRevision = 1L });
            db.AssetVersions.Add(new AssetVersion { Id = Guid.NewGuid(), AssetId = noncurrentId, VersionNumber = 1, IsCurrent = false, StorageKey = "k2a", FileName = "f2a", ContentSha256 = new string('2', 64), ContentLength = 100, ReleaseNotes = "r", LicenseCode = AssetLicenseCode.PERSONAL, LicenseTemplateVersion = "1.0", LicenseDisplayName = "L", LicenseTerms = "T", ProcessingStatus = AssetVersionProcessingStatus.READY, ProcessingUpdatedAt = now, CreatedAt = now });
            db.AssetVersions.Add(new AssetVersion { Id = Guid.NewGuid(), AssetId = noncurrentId, VersionNumber = 2, IsCurrent = false, StorageKey = "k2b", FileName = "f2b", ContentSha256 = new string('2', 64), ContentLength = 100, ReleaseNotes = "r", LicenseCode = AssetLicenseCode.PERSONAL, LicenseTemplateVersion = "1.0", LicenseDisplayName = "L", LicenseTerms = "T", ProcessingStatus = AssetVersionProcessingStatus.PENDING_INSPECTION, ProcessingUpdatedAt = now, CreatedAt = now });
            db.AssetEmbeddings.Add(new AssetEmbedding { Id = Guid.NewGuid(), AssetId = noncurrentId, ModelKey = modelKey, Provider = "Ollama", ModelId = "model", ModelRevision = "test-rev", ModelDigest = embeddingOptions.Digest, Dimension = 768, ContentSchemaVersion = "v1", SourceRevision = 1L, ContentHash = new string('2', 64), Embedding = vector, CreatedAt = now, UpdatedAt = now });

            // 3. Stale embedding asset: current version is READY, but SourceRevision != SearchRevision
            var staleId = Guid.NewGuid();
            db.Assets.Add(new Asset { Id = staleId, AuthorId = authorId, CategoryId = categoryId, Title = "Stale Embedding Asset", Description = "Desc", Price = 10m, CreatedAt = now, UpdatedAt = now, SearchRevision = 2L });
            db.AssetVersions.Add(new AssetVersion { Id = Guid.NewGuid(), AssetId = staleId, VersionNumber = 1, IsCurrent = true, StorageKey = "k3", FileName = "f3", ContentSha256 = new string('3', 64), ContentLength = 100, ReleaseNotes = "r", LicenseCode = AssetLicenseCode.PERSONAL, LicenseTemplateVersion = "1.0", LicenseDisplayName = "L", LicenseTerms = "T", ProcessingStatus = AssetVersionProcessingStatus.READY, ProcessingUpdatedAt = now, CreatedAt = now });
            db.AssetEmbeddings.Add(new AssetEmbedding { Id = Guid.NewGuid(), AssetId = staleId, ModelKey = modelKey, Provider = "Ollama", ModelId = "model", ModelRevision = "test-rev", ModelDigest = embeddingOptions.Digest, Dimension = 768, ContentSchemaVersion = "v1", SourceRevision = 1L, ContentHash = new string('3', 64), Embedding = vector, CreatedAt = now, UpdatedAt = now }); // SourceRevision 1 != SearchRevision 2

            // 4. Wrong model asset: current version is READY, but ModelKey != modelKey
            var wrongModelId = Guid.NewGuid();
            var differentModelKey = new string('f', 64);
            db.Assets.Add(new Asset { Id = wrongModelId, AuthorId = authorId, CategoryId = categoryId, Title = "Wrong Model Asset", Description = "Desc", Price = 10m, CreatedAt = now, UpdatedAt = now, SearchRevision = 1L });
            db.AssetVersions.Add(new AssetVersion { Id = Guid.NewGuid(), AssetId = wrongModelId, VersionNumber = 1, IsCurrent = true, StorageKey = "k4", FileName = "f4", ContentSha256 = new string('4', 64), ContentLength = 100, ReleaseNotes = "r", LicenseCode = AssetLicenseCode.PERSONAL, LicenseTemplateVersion = "1.0", LicenseDisplayName = "L", LicenseTerms = "T", ProcessingStatus = AssetVersionProcessingStatus.READY, ProcessingUpdatedAt = now, CreatedAt = now });
            db.AssetEmbeddings.Add(new AssetEmbedding { Id = Guid.NewGuid(), AssetId = wrongModelId, ModelKey = differentModelKey, Provider = "Ollama", ModelId = "model", ModelRevision = "test-rev", ModelDigest = embeddingOptions.Digest, Dimension = 768, ContentSchemaVersion = "v1", SourceRevision = 1L, ContentHash = new string('4', 64), Embedding = vector, CreatedAt = now, UpdatedAt = now });

            // 5. Soft-deleted asset: DeletedAt != null, current version is READY, valid embedding
            var deletedId = Guid.NewGuid();
            db.Assets.Add(new Asset { Id = deletedId, AuthorId = authorId, CategoryId = categoryId, Title = "Deleted Asset", Description = "Desc", Price = 10m, CreatedAt = now, UpdatedAt = now, DeletedAt = now, SearchRevision = 1L });
            db.AssetVersions.Add(new AssetVersion { Id = Guid.NewGuid(), AssetId = deletedId, VersionNumber = 1, IsCurrent = true, StorageKey = "k5", FileName = "f5", ContentSha256 = new string('5', 64), ContentLength = 100, ReleaseNotes = "r", LicenseCode = AssetLicenseCode.PERSONAL, LicenseTemplateVersion = "1.0", LicenseDisplayName = "L", LicenseTerms = "T", ProcessingStatus = AssetVersionProcessingStatus.READY, ProcessingUpdatedAt = now, CreatedAt = now });
            db.AssetEmbeddings.Add(new AssetEmbedding { Id = Guid.NewGuid(), AssetId = deletedId, ModelKey = modelKey, Provider = "Ollama", ModelId = "model", ModelRevision = "test-rev", ModelDigest = embeddingOptions.Digest, Dimension = 768, ContentSchemaVersion = "v1", SourceRevision = 1L, ContentHash = new string('5', 64), Embedding = vector, CreatedAt = now, UpdatedAt = now });

            await db.SaveChangesAsync();
        }

        // Act: Execute CountEligibleAssetsAsync against real PostgreSQL
        var req = new GetAssetsRequest { Search = "Asset" };
        (var filterEligible, var semanticEligible) = await SearchSqlProfiler.CountEligibleAssetsAsync(fixture, req, modelKey, CancellationToken.None);

        // Assert:
        // Filter-eligible must be 3 (validId, staleId, wrongModelId) - noncurrentId and deletedId are excluded
        // Semantic-eligible must be 1 (validId) - staleId (SourceRevision mismatch) and wrongModelId (ModelKey mismatch) are excluded
        filterEligible.Should().Be(3);
        semanticEligible.Should().Be(1);
    }
}
