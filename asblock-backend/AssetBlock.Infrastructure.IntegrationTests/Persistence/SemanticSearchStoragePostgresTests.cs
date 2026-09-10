using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence;

[Collection(nameof(PostgresStoreCollection))]
public sealed class SemanticSearchStoragePostgresTests(PostgresFixture fixture)
{
    private const string VALID_HEX_64 = "e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6";
    private const string VALID_DIGEST = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6";
    private const int DIMENSION_768 = 768;

    [Fact]
    public async Task MigrateAsync_ShouldCreateAssetEmbeddingsSchemaWithChecksAndIndexes()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        var hasTable = await db.Database.SqlQueryRaw<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = 'asset_embeddings'
            ) AS "Value"
            """).SingleAsync();
        hasTable.Should().BeTrue();

        List<string> columns = await db.Database.SqlQueryRaw<string>(
            """
            SELECT column_name || ':' || udt_name AS "Value"
            FROM information_schema.columns
            WHERE table_name = 'asset_embeddings'
            """).ToListAsync();

        columns.Should().Contain("Id:uuid");
        columns.Should().Contain("AssetId:uuid");
        columns.Should().Contain("ModelKey:bpchar");
        columns.Should().Contain("Dimension:int4");
        columns.Should().Contain("SourceRevision:int8");
        columns.Should().Contain("ContentHash:bpchar");
        columns.Should().Contain("Embedding:vector");

        List<string> checks = await db.Database.SqlQueryRaw<string>(
            """
            SELECT conname AS "Value"
            FROM pg_constraint
            WHERE contype = 'c'
              AND conrelid = 'asset_embeddings'::regclass
            """).ToListAsync();

        checks.Should().Contain([
            "CK_asset_embeddings_model_key",
            "CK_asset_embeddings_content_hash",
            "CK_asset_embeddings_model_digest",
            "CK_asset_embeddings_dimension",
            "CK_asset_embeddings_source_revision",
            "CK_asset_embeddings_vector_dims"
        ]);

        List<string> indexes = await db.Database.SqlQueryRaw<string>(
            """
            SELECT indexname AS "Value"
            FROM pg_indexes
            WHERE tablename = 'asset_embeddings'
            """).ToListAsync();

        indexes.Should().Contain("UIX_asset_embeddings_asset_id_model_key");
        indexes.Should().Contain("IX_asset_embeddings_model_key_asset_id");
    }

    [Fact]
    public async Task MigrateAsync_ShouldCreateJobConstraintsAndSearchRevision()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        List<string> assetChecks = await db.Database.SqlQueryRaw<string>(
            """
            SELECT conname AS "Value"
            FROM pg_constraint
            WHERE contype = 'c'
              AND conrelid = 'assets'::regclass
            """).ToListAsync();

        assetChecks.Should().Contain("CK_assets_search_revision");

        List<string> jobChecks = await db.Database.SqlQueryRaw<string>(
            """
            SELECT conname AS "Value"
            FROM pg_constraint
            WHERE contype = 'c'
              AND conrelid = 'asset_processing_jobs'::regclass
            """).ToListAsync();

        jobChecks.Should().Contain("CK_asset_processing_jobs_embedding_hashes");

        List<string> jobIndexes = await db.Database.SqlQueryRaw<string>(
            """
            SELECT indexname AS "Value"
            FROM pg_indexes
            WHERE tablename = 'asset_processing_jobs'
            """).ToListAsync();

        jobIndexes.Should().Contain("UIX_asset_processing_jobs_embedding_active");
        jobIndexes.Should().Contain("UIX_asset_processing_jobs_idempotency");
    }

    [Fact]
    public async Task AssetEmbedding_InsertAndQuery_SucceedsAndCascadesOnAssetDelete()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        db.Users.Add(new User
        {
            Id = authorId,
            Username = "author_embed",
            Email = "embed_author@test.com",
            PasswordHash = "hash",
            Role = AppRoles.USER,
            CreatedAt = now
        });

        db.Categories.Add(new Category
        {
            Id = categoryId,
            Name = "Embed Category",
            Slug = "embed-category",
            Description = "Desc",
            CreatedAt = now
        });

        var asset = new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = "Vector Asset",
            Description = "Vector Description",
            Price = 10m,
            CreatedAt = now
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        asset.SearchRevision.Should().Be(1L);

        var vectorFloats = new float[DIMENSION_768];
        vectorFloats[0] = 0.5f;
        vectorFloats[DIMENSION_768 - 1] = 0.5f;

        var embedding = new AssetEmbedding
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            ModelKey = VALID_HEX_64,
            Provider = "Ollama",
            ModelId = "embeddinggemma:300m-qat-q8_0",
            ModelRevision = "manifest-e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            ModelDigest = VALID_DIGEST,
            Dimension = DIMENSION_768,
            ContentSchemaVersion = "asset-public-metadata-v1",
            SourceRevision = 1L,
            ContentHash = VALID_HEX_64,
            Embedding = new Vector(vectorFloats)
        };

        db.AssetEmbeddings.Add(embedding);
        await db.SaveChangesAsync();

        AssetEmbedding loaded = await db.AssetEmbeddings.SingleAsync(e => e.Id == embedding.Id);
        loaded.AssetId.Should().Be(assetId);
        loaded.ModelKey.Should().Be(VALID_HEX_64);
        loaded.Dimension.Should().Be(DIMENSION_768);
        loaded.Embedding.ToArray().Length.Should().Be(DIMENSION_768);
        loaded.CreatedAt.Should().BeCloseTo(now, TimeSpan.FromSeconds(5));

        // Cascade delete
        db.Assets.Remove(asset);
        await db.SaveChangesAsync();

        var remainingEmbeddings = await db.AssetEmbeddings.CountAsync(e => e.AssetId == assetId);
        remainingEmbeddings.Should().Be(0);
    }

    [Fact]
    public async Task AssetEmbedding_ConstraintViolations_ShouldThrowPostgresException()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        db.Users.Add(new User { Id = authorId, Username = "auth_cv", Email = "auth_cv@test.com", PasswordHash = "h", Role = AppRoles.USER, CreatedAt = now });
        db.Categories.Add(new Category { Id = categoryId, Name = "Cat CV", Slug = "cat-cv", CreatedAt = now });
        db.Assets.Add(new Asset { Id = assetId, AuthorId = authorId, CategoryId = categoryId, Title = "Title", Price = 5m, CreatedAt = now });
        await db.SaveChangesAsync();

        // 1. Invalid ModelKey (not 64 hex chars)
        AssetEmbedding badKeyEmbedding = CreateValidEmbedding(assetId);
        badKeyEmbedding.ModelKey = "NOT_HEX";
        db.AssetEmbeddings.Add(badKeyEmbedding);
        Func<Task> actBadKey = async () => await db.SaveChangesAsync();
        await actBadKey.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.ConstraintName == "CK_asset_embeddings_model_key");
        db.ChangeTracker.Clear();

        // 2. Invalid Dimension (not 768)
        AssetEmbedding badDimEmbedding = CreateValidEmbedding(assetId);
        badDimEmbedding.Dimension = 512;
        db.AssetEmbeddings.Add(badDimEmbedding);
        Func<Task> actBadDim = async () => await db.SaveChangesAsync();
        await actBadDim.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.ConstraintName == "CK_asset_embeddings_dimension");
        db.ChangeTracker.Clear();

        // 3. Invalid SourceRevision (<= 0)
        AssetEmbedding badRevEmbedding = CreateValidEmbedding(assetId);
        badRevEmbedding.SourceRevision = 0;
        db.AssetEmbeddings.Add(badRevEmbedding);
        Func<Task> actBadRev = async () => await db.SaveChangesAsync();
        await actBadRev.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.ConstraintName == "CK_asset_embeddings_source_revision");
        db.ChangeTracker.Clear();

        // 4. Duplicate (AssetId, ModelKey)
        AssetEmbedding emb1 = CreateValidEmbedding(assetId);
        AssetEmbedding emb2 = CreateValidEmbedding(assetId);
        db.AssetEmbeddings.AddRange(emb1, emb2);
        Func<Task> actDup = async () => await db.SaveChangesAsync();
        await actDup.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.ConstraintName == "UIX_asset_embeddings_asset_id_model_key");
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task AssetProcessingJob_EmbeddingHashesConstraint_Enforced()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        db.Users.Add(new User { Id = authorId, Username = "auth_job", Email = "auth_job@test.com", PasswordHash = "h", Role = AppRoles.USER, CreatedAt = now });
        db.Categories.Add(new Category { Id = categoryId, Name = "Cat Job", Slug = "cat-job", CreatedAt = now });
        db.Assets.Add(new Asset { Id = assetId, AuthorId = authorId, CategoryId = categoryId, Title = "Title", Price = 5m, CreatedAt = now });
        db.AssetVersions.Add(new AssetVersion
        {
            Id = versionId,
            AssetId = assetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "s",
            FileName = "f",
            ContentLength = 1,
            ContentSha256 = "c",
            ReleaseNotes = "r",
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "Terms",
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = now,
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        // Non-embedding job with InputHash set must fail CK_asset_processing_jobs_embedding_hashes
        var nonEmbeddingJob = new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            AssetVersionId = versionId,
            Type = AssetProcessingJobType.ARCHIVE_INSPECTION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.QUEUED,
            Stage = "QUEUED",
            AttemptCount = 0,
            MaxAttempts = 3,
            AvailableAt = now,
            Payload = "{}",
            InputHash = VALID_HEX_64 // invalid for non-embedding job!
        };
        db.AssetProcessingJobs.Add(nonEmbeddingJob);
        Func<Task> actNonEmbed = async () => await db.SaveChangesAsync();
        await actNonEmbed.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.ConstraintName == "CK_asset_processing_jobs_embedding_hashes");
        db.ChangeTracker.Clear();

        // Embedding job with valid InputHash and ModelKey succeeds
        var embeddingJob = new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            AssetVersionId = versionId,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.QUEUED,
            Stage = "QUEUED",
            AttemptCount = 0,
            MaxAttempts = 3,
            AvailableAt = now,
            Payload = "{}",
            InputHash = VALID_HEX_64,
            ModelKey = VALID_HEX_64
        };
        db.AssetProcessingJobs.Add(embeddingJob);
        await db.SaveChangesAsync();

        // Duplicate active embedding job must fail UIX_asset_processing_jobs_embedding_active
        var duplicateActiveJob = new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            AssetVersionId = versionId,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.RUNNING,
            Stage = "RUNNING",
            AttemptCount = 1,
            MaxAttempts = 3,
            AvailableAt = now,
            LeaseOwner = "worker-1",
            LeaseToken = Guid.NewGuid(),
            LeaseExpiresAt = now.AddMinutes(5),
            Payload = "{}",
            InputHash = VALID_HEX_64,
            ModelKey = VALID_HEX_64
        };
        db.AssetProcessingJobs.Add(duplicateActiveJob);
        Func<Task> actDupActive = async () => await db.SaveChangesAsync();
        await actDupActive.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.ConstraintName == "UIX_asset_processing_jobs_embedding_active");
    }

    [Fact]
    public async Task VectorSearchCapability_WhenExtensionPresent_ReportsAvailableAndDegradesCleanly()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        IOptions<EmbeddingOptions> options = Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions
        {
            Enabled = true,
            Provider = "Ollama",
            Model = "embeddinggemma:300m-qat-q8_0",
            Revision = "manifest-e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            Digest = VALID_DIGEST,
            Dimension = DIMENSION_768,
            ContentSchemaVersion = "asset-public-metadata-v1"
        });

        var capability = new VectorSearchCapability(
            new TestDbContextFactory(fixture),
            options,
            NullLogger<VectorSearchCapability>.Instance);

        VectorSearchCapabilityResult result = await capability.CheckCapability();
        result.IsAvailable.Should().BeTrue(result.Reason);
        result.HasExtension.Should().BeTrue();
        result.ModelKey.Should().NotBeNullOrWhiteSpace();

        // Verify that lexical catalog search still functions normally without provider I/O
        var assetStore = new AssetStore(db);
        var request = new GetAssetsRequest
        {
            Search = "test",
            Page = 1,
            PageSize = 10
        };

        CatalogPageResult<AssetListItem> pagedResult = await assetStore.GetPaged(request);
        pagedResult.Items.Should().BeEmpty();
        pagedResult.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task GetPaged_WithHybridRrf_ShouldRankByRrfScoreAndDeterministicTieBreakers()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetStore = new AssetStore(db);

        var queryVector = new float[DIMENSION_768];
        queryVector[0] = 1.0f;

        Asset assetSem = TestData.CreateAsset(author.Id, category.Id, title: "Sci-Fi Katana Blade", price: 10m);
        Asset assetLex = TestData.CreateAsset(author.Id, category.Id, title: "Cyberpunk Katana", price: 20m);
        Asset assetBoth = TestData.CreateAsset(author.Id, category.Id, title: "Cyberpunk Katana Sword", price: 30m);

        db.Assets.AddRange(assetSem, assetLex, assetBoth);
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(assetSem.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY),
            TestData.CreateAssetVersion(assetLex.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY),
            TestData.CreateAssetVersion(assetBoth.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY));

        var vecSem = new float[DIMENSION_768];
        vecSem[0] = 0.99f;
        vecSem[1] = 0.1f;

        var vecLex = new float[DIMENSION_768];
        vecLex[1] = 1.0f;

        var vecBoth = new float[DIMENSION_768];
        vecBoth[0] = 1.0f;

        AssetEmbedding embSem = CreateValidEmbedding(assetSem.Id);
        embSem.Embedding = new Vector(vecSem);

        AssetEmbedding embLex = CreateValidEmbedding(assetLex.Id);
        embLex.Embedding = new Vector(vecLex);

        AssetEmbedding embBoth = CreateValidEmbedding(assetBoth.Id);
        embBoth.Embedding = new Vector(vecBoth);

        db.AssetEmbeddings.AddRange(embSem, embLex, embBoth);
        await db.SaveChangesAsync();

        var request = new GetAssetsRequest
        {
            Search = "Cyberpunk Katana",
            Page = 1,
            PageSize = 10
        };

        CatalogPageResult<AssetListItem> result = await assetStore.GetPaged(request, queryVector, VALID_HEX_64);

        result.Items.Should().NotBeEmpty();
        result.IsTruncated.Should().BeFalse();
        result.Items[0].Id.Should().Be(assetBoth.Id);
    }

    [Fact]
    public async Task GetPaged_WithSentinels_WhenCandidatesExceed200_ShouldSetIsTruncatedTrue()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetStore = new AssetStore(db);

        var queryVector = new float[DIMENSION_768];
        queryVector[0] = 1.0f;

        var assets = new List<Asset>(202);
        var versions = new List<AssetVersion>(202);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 202; i++)
        {
            Asset asset = TestData.CreateAsset(author.Id, category.Id, title: $"SentinelPack Item {i}", createdAt: now.AddSeconds(i));
            assets.Add(asset);
            versions.Add(TestData.CreateAssetVersion(asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY));
        }

        db.Assets.AddRange(assets);
        db.AssetVersions.AddRange(versions);
        await db.SaveChangesAsync();

        var request = new GetAssetsRequest
        {
            Search = "SentinelPack",
            Page = 1,
            PageSize = 10
        };

        CatalogPageResult<AssetListItem> result = await assetStore.GetPaged(request, queryVector, VALID_HEX_64);

        result.IsTruncated.Should().BeTrue();
        result.TotalCount.Should().Be(200);
        result.Items.Count.Should().Be(10);
    }

    [Fact]
    public async Task GetPaged_WithNonReadyVersionOrDeletedAsset_ShouldExcludeFromBothLexicalAndSemantic()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetStore = new AssetStore(db);

        var queryVector = new float[DIMENSION_768];
        queryVector[0] = 1.0f;

        Asset readyAsset = TestData.CreateAsset(author.Id, category.Id, title: "Laser Blaster Ready");
        Asset pendingAsset = TestData.CreateAsset(author.Id, category.Id, title: "Laser Blaster Pending");
        Asset deletedAsset = TestData.CreateAsset(author.Id, category.Id, title: "Laser Blaster Deleted");

        db.Assets.AddRange(readyAsset, pendingAsset, deletedAsset);
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(readyAsset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY),
            TestData.CreateAssetVersion(pendingAsset.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION),
            TestData.CreateAssetVersion(deletedAsset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY));

        AssetEmbedding embReady = CreateValidEmbedding(readyAsset.Id);
        embReady.Embedding = new Vector(queryVector);
        AssetEmbedding embPending = CreateValidEmbedding(pendingAsset.Id);
        embPending.Embedding = new Vector(queryVector);
        AssetEmbedding embDeleted = CreateValidEmbedding(deletedAsset.Id);
        embDeleted.Embedding = new Vector(queryVector);

        db.AssetEmbeddings.AddRange(embReady, embPending, embDeleted);
        await db.SaveChangesAsync();

        await assetStore.SoftDelete(deletedAsset.Id, DateTimeOffset.UtcNow);

        var request = new GetAssetsRequest
        {
            Search = "Laser Blaster",
            Page = 1,
            PageSize = 10
        };

        CatalogPageResult<AssetListItem> result = await assetStore.GetPaged(request, queryVector, VALID_HEX_64);

        result.Items.Should().ContainSingle();
        result.Items[0].Id.Should().Be(readyAsset.Id);
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetPaged_WithExplicitSort_ShouldBypassSemanticRetrieval()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetStore = new AssetStore(db);

        var queryVector = new float[DIMENSION_768];
        queryVector[0] = 1.0f;

        Asset cheap = TestData.CreateAsset(author.Id, category.Id, title: "Space Drone Alpha", price: 5m);
        Asset expensive = TestData.CreateAsset(author.Id, category.Id, title: "Space Drone Beta", price: 50m);

        db.Assets.AddRange(cheap, expensive);
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(cheap.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY),
            TestData.CreateAssetVersion(expensive.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY));

        AssetEmbedding embExpensive = CreateValidEmbedding(expensive.Id);
        embExpensive.Embedding = new Vector(queryVector);
        var cheapVec = new float[DIMENSION_768];
        cheapVec[1] = 1.0f;
        AssetEmbedding embCheap = CreateValidEmbedding(cheap.Id);
        embCheap.Embedding = new Vector(cheapVec);

        db.AssetEmbeddings.AddRange(embExpensive, embCheap);
        await db.SaveChangesAsync();

        var request = new GetAssetsRequest
        {
            Search = "Space Drone",
            SortBy = "Price",
            SortDirection = SortDirection.ASC,
            Page = 1,
            PageSize = 10
        };

        CatalogPageResult<AssetListItem> result = await assetStore.GetPaged(request, queryVector, VALID_HEX_64);

        result.Items.Should().HaveCount(2);
        result.Items[0].Id.Should().Be(cheap.Id);
        result.Items[1].Id.Should().Be(expensive.Id);
        result.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task GetPaged_WhenHybridQueryExecuted_ShouldFuseLexicalAndSemanticRankingsWithTrigramMatches()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetStore = new AssetStore(db);

        var queryVector = new float[DIMENSION_768];
        queryVector[0] = 1.0f;

        // Asset 1: strong semantic match (vector matches queryVector), weak title match
        Asset semanticStrong = TestData.CreateAsset(author.Id, category.Id, title: "Galaxy Starship Explorer", price: 10m);
        // Asset 2: typo title match (Celestil Blade -> Celestial via trigram similarity ~0.39), has NO embedding
        Asset trigramLexical = TestData.CreateAsset(author.Id, category.Id, title: "Celestil Blade", price: 20m);

        db.Assets.AddRange(semanticStrong, trigramLexical);
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(semanticStrong.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY),
            TestData.CreateAssetVersion(trigramLexical.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY));

        AssetEmbedding embSemantic = CreateValidEmbedding(semanticStrong.Id);
        embSemantic.Embedding = new Vector(queryVector); // cosine distance = 0

        // Only semanticStrong has an embedding; trigramLexical has NO embedding and must be retrieved via trigram lexical candidate branch
        db.AssetEmbeddings.Add(embSemantic);
        await db.SaveChangesAsync();

        var request = new GetAssetsRequest
        {
            Search = "Celestial",
            Page = 1,
            PageSize = 10
        };

        CatalogPageResult<AssetListItem> result = await assetStore.GetPaged(request, queryVector, VALID_HEX_64);

        result.TotalCount.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items.Select(a => a.Id).Should().Contain([semanticStrong.Id, trigramLexical.Id]);
        result.IsTruncated.Should().BeFalse();
    }

    private static AssetEmbedding CreateValidEmbedding(Guid assetId)
    {
        return new AssetEmbedding
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            ModelKey = VALID_HEX_64,
            Provider = "Ollama",
            ModelId = "embeddinggemma:300m-qat-q8_0",
            ModelRevision = "manifest-e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            ModelDigest = VALID_DIGEST,
            Dimension = DIMENSION_768,
            ContentSchemaVersion = "asset-public-metadata-v1",
            SourceRevision = 1L,
            ContentHash = VALID_HEX_64,
            Embedding = new Vector(new float[DIMENSION_768])
        };
    }

    private sealed class TestDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(fixture.CreateDbContext());
    }
}
