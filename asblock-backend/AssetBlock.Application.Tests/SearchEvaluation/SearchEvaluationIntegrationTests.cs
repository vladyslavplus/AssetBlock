using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using AssetBlock.SearchEvaluation.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Application.Tests.SearchEvaluation;

[Collection("SearchEvaluationIntegrationTests")]
public class SearchEvaluationIntegrationTests
{
    private static EmbeddingOptions CreateTestEmbeddingOptions() => new()
    {
        Enabled = true,
        Provider = "LocalOllama",
        Model = "embeddinggemma:300m-qat-q8_0",
        Revision = "test-rev",
        Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        Dimension = 768,
        BackfillBatchSize = 10,
        ContentSchemaVersion = "asset-public-metadata-v1"
    };

    [Fact]
    public async Task CategoryAndTag_SearchRetrieval_WhenOnlyMatchingMetadata_IsRetrievableOnIsolatedDb()
    {
        await using SearchEvaluationDbFixture fixture = new();
        await fixture.InitializeAsync();

        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        EmbeddingOptions embeddingOptions = CreateTestEmbeddingOptions();
        var modelKey = EmbeddingModelKey.Compute(embeddingOptions);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var dummyVector = new float[768];
        dummyVector[0] = 1f;

        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            db.Users.Add(new User
            {
                Id = authorId,
                Username = "author_test",
                Email = "author@example.com",
                PasswordHash = "hash",
                Role = AppRoles.USER,
                CreatedAt = now
            });

            db.Categories.Add(new Category
            {
                Id = categoryId,
                Name = "SpecialtyCyberpunk",
                Slug = "specialty-cyberpunk",
                CreatedAt = now
            });

            db.Tags.Add(new Tag
            {
                Id = tagId,
                Name = "neonkatana",
                CreatedAt = now
            });

            db.Assets.Add(new Asset
            {
                Id = assetId,
                AuthorId = authorId,
                CategoryId = categoryId,
                Title = "Generic Mesh Object 42",
                Description = "Unrelated geometric object description.",
                Price = 10m,
                SearchRevision = 1L,
                CreatedAt = now,
                UpdatedAt = now
            });

            db.AssetTags.Add(new AssetTag
            {
                AssetId = assetId,
                TagId = tagId
            });

            db.AssetVersions.Add(new AssetVersion
            {
                Id = versionId,
                AssetId = assetId,
                VersionNumber = 1,
                IsCurrent = true,
                StorageKey = "test/v1.zip",
                FileName = "asset.zip",
                ContentLength = 1024,
                ContentSha256 = new string('1', 64),
                ReleaseNotes = "Initial",
                LicenseCode = AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Standard",
                LicenseTerms = "Terms",
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
                ModelId = "test-model",
                ModelRevision = embeddingOptions.Revision,
                ModelDigest = embeddingOptions.Digest,
                Dimension = 768,
                ContentSchemaVersion = "v1",
                SourceRevision = 1L,
                ContentHash = new string('0', 64),
                Embedding = new Vector(dummyVector),
                CreatedAt = now,
                UpdatedAt = now
            });

            await db.SaveChangesAsync();
        }

        // Test retrieval via AssetStore
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var store = new AssetStore(db);

            // 1. Hybrid semantic retrieval matching category/tag query via embedding
            var hybridReq = new GetAssetsRequest { Search = "SpecialtyCyberpunk", Page = 1, PageSize = 20 };
            CatalogPageResult<AssetListItem> hybridPage = await store.GetPaged(hybridReq, dummyVector, modelKey, CancellationToken.None);
            hybridPage.Items.Should().Contain(i => i.Id == assetId, "Asset must be retrievable via hybrid search when query matches category/tag metadata.");

            // 2. Search matching tag filter
            var tagFilterReq = new GetAssetsRequest { Tags = ["neonkatana"], Page = 1, PageSize = 20 };
            CatalogPageResult<AssetListItem> tagFilterPage = await store.GetPaged(tagFilterReq, null, null, CancellationToken.None);
            tagFilterPage.Items.Should().Contain(i => i.Id == assetId, "Asset must be retrievable by exact tag filter.");

            // 3. Search matching category filter
            var categoryFilterReq = new GetAssetsRequest { CategoryId = categoryId, Page = 1, PageSize = 20 };
            CatalogPageResult<AssetListItem> categoryFilterPage = await store.GetPaged(categoryFilterReq, null, null, CancellationToken.None);
            categoryFilterPage.Items.Should().Contain(i => i.Id == assetId, "Asset must be retrievable by exact category filter.");
        }
    }

    [Fact]
    public async Task Backfill_Invariants_OnIsolatedContainer_VerifiesCursorProgressionDuplicateEnqueueAndStaleFinalization()
    {
        await using SearchEvaluationDbFixture fixture = new();
        await fixture.InitializeAsync();

        EmbeddingOptions embeddingOptions = CreateTestEmbeddingOptions();
        embeddingOptions.BackfillBatchSize = 3;
        var modelKey = EmbeddingModelKey.Compute(embeddingOptions);
        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            db.Users.Add(new User
            {
                Id = authorId,
                Username = "backfill_tester",
                Email = "tester@example.com",
                PasswordHash = "hash",
                Role = AppRoles.USER,
                CreatedAt = now
            });

            db.Categories.Add(new Category
            {
                Id = categoryId,
                Name = "BackfillCategory",
                Slug = "backfill-category",
                CreatedAt = now
            });

            // Seed 5 eligible assets
            for (var i = 0; i < 5; i++)
            {
                var aId = Guid.NewGuid();
                db.Assets.Add(new Asset
                {
                    Id = aId,
                    AuthorId = authorId,
                    CategoryId = categoryId,
                    Title = $"Backfill Asset {i}",
                    Description = $"Description for asset {i}",
                    Price = 5m,
                    SearchRevision = 1L,
                    CreatedAt = now,
                    UpdatedAt = now
                });

                db.AssetVersions.Add(new AssetVersion
                {
                    Id = Guid.NewGuid(),
                    AssetId = aId,
                    VersionNumber = 1,
                    IsCurrent = true,
                    StorageKey = $"assets/{i}/v1.zip",
                    FileName = $"asset{i}.zip",
                    ContentLength = 100,
                    ContentSha256 = new string((char)('0' + i), 64),
                    ReleaseNotes = "Initial",
                    LicenseCode = AssetLicenseCode.PERSONAL,
                    LicenseTemplateVersion = "1.0",
                    LicenseDisplayName = "Standard",
                    LicenseTerms = "Terms",
                    ProcessingStatus = AssetVersionProcessingStatus.READY,
                    ProcessingUpdatedAt = now,
                    CreatedAt = now
                });
            }

            await db.SaveChangesAsync();
        }

        MemoryCacheService cacheService = new();
        EmbeddingBackfillCoordinator.ResetFallbackCursors();
        Microsoft.Extensions.Options.IOptions<AssetProcessingOptions> jobOptions = MsOptions.Create(new AssetProcessingOptions());

        // 1. Initial cursor is 0
        int cursor0;
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, jobOptions);
            var fakeGen = new FakeEmbeddingGenerator(embeddingOptions, modelKey);
            var coord = new EmbeddingBackfillCoordinator(
                db, jobStore, fakeGen, MsOptions.Create(embeddingOptions), NullLogger<EmbeddingBackfillCoordinator>.Instance, TimeProvider.System, cacheService);

            cursor0 = await coord.GetCursorOffset(modelKey, CancellationToken.None);
        }
        cursor0.Should().Be(0);

        // 2. Cycle 1: enqueues batch of 3 assets, cursor progresses from 0 to 3
        int enqueued1;
        int cursor1;
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, jobOptions);
            var fakeGen = new FakeEmbeddingGenerator(embeddingOptions, modelKey);
            var coord = new EmbeddingBackfillCoordinator(
                db, jobStore, fakeGen, MsOptions.Create(embeddingOptions), NullLogger<EmbeddingBackfillCoordinator>.Instance, TimeProvider.System, cacheService);

            enqueued1 = await coord.RunBackfillCycle(CancellationToken.None);
            cursor1 = await coord.GetCursorOffset(modelKey, CancellationToken.None);
        }
        enqueued1.Should().Be(3);
        cursor1.Should().Be(3);

        // 3. Claim batch 1 and verify stale finalization rejection (LEASE_LOST), then finalize batch 1
        bool staleRejected;
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, jobOptions);
            var finalizer = new AssetEmbeddingFinalizer(db, cacheService, NullLogger<AssetEmbeddingFinalizer>.Instance);

            IReadOnlyList<ClaimedAssetProcessingJob> claimed = await jobStore.ClaimPendingBatch(
                batchSize: 10,
                leaseDuration: TimeSpan.FromMinutes(5),
                leaseOwner: "test-worker",
                cancellationToken: CancellationToken.None);

            claimed.Should().HaveCount(3);

            // Stale finalization attempt with invalid LeaseToken
            ClaimedAssetProcessingJob firstClaim = claimed[0];
            var staleParams = new FinalizeEmbeddingParameters(
                firstClaim.JobId,
                Guid.NewGuid(), // Stale lease token
                firstClaim.AssetId,
                firstClaim.AssetVersionId,
                SourceRevision: 1,
                ContentHash: new string('9', 64),
                modelKey,
                embeddingOptions.Provider,
                embeddingOptions.Model,
                embeddingOptions.Revision,
                embeddingOptions.Digest,
                embeddingOptions.Dimension,
                embeddingOptions.ContentSchemaVersion,
                new float[embeddingOptions.Dimension]);

            EmbeddingFinalizationStatus staleStatus = await finalizer.Finalize(staleParams, CancellationToken.None);
            staleStatus.Should().Be(EmbeddingFinalizationStatus.LEASE_LOST, "Stale or lost lease token must be rejected with LEASE_LOST.");
            staleRejected = true;

            // Finalize with valid tokens
            foreach (ClaimedAssetProcessingJob claim in claimed)
            {
                Asset? asset = await db.Assets.FindAsync(claim.AssetId);
                CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(
                    asset!.Title,
                    asset.Description,
                    "BackfillCategory",
                    []);

                var validParams = new FinalizeEmbeddingParameters(
                    claim.JobId,
                    claim.LeaseToken,
                    claim.AssetId,
                    claim.AssetVersionId,
                    SourceRevision: 1,
                    canonical.ContentHash,
                    modelKey,
                    embeddingOptions.Provider,
                    embeddingOptions.Model,
                    embeddingOptions.Revision,
                    embeddingOptions.Digest,
                    embeddingOptions.Dimension,
                    embeddingOptions.ContentSchemaVersion,
                    new float[embeddingOptions.Dimension]);

                EmbeddingFinalizationStatus status = await finalizer.Finalize(validParams, CancellationToken.None);
                status.Should().Be(EmbeddingFinalizationStatus.COMMITTED);
            }
        }
        staleRejected.Should().BeTrue();

        // 4. Cycle 2: enqueues remaining 2 assets, cursor wraps back to 0
        int enqueued2;
        int cursor2;
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, jobOptions);
            var fakeGen = new FakeEmbeddingGenerator(embeddingOptions, modelKey);
            var coord = new EmbeddingBackfillCoordinator(
                db, jobStore, fakeGen, MsOptions.Create(embeddingOptions), NullLogger<EmbeddingBackfillCoordinator>.Instance, TimeProvider.System, cacheService);

            enqueued2 = await coord.RunBackfillCycle(CancellationToken.None);
            cursor2 = await coord.GetCursorOffset(modelKey, CancellationToken.None);
        }
        enqueued2.Should().Be(2);
        cursor2.Should().Be(0, "Cursor must wrap to 0 once catalog has been fully traversed.");

        // Claim and finalize remaining 2 jobs
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, jobOptions);
            var finalizer = new AssetEmbeddingFinalizer(db, cacheService, NullLogger<AssetEmbeddingFinalizer>.Instance);

            IReadOnlyList<ClaimedAssetProcessingJob> claimed = await jobStore.ClaimPendingBatch(
                batchSize: 10,
                leaseDuration: TimeSpan.FromMinutes(5),
                leaseOwner: "test-worker",
                cancellationToken: CancellationToken.None);

            claimed.Should().HaveCount(2);

            foreach (ClaimedAssetProcessingJob claim in claimed)
            {
                Asset? asset = await db.Assets.FindAsync(claim.AssetId);
                CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(
                    asset!.Title,
                    asset.Description,
                    "BackfillCategory",
                    []);

                var validParams = new FinalizeEmbeddingParameters(
                    claim.JobId,
                    claim.LeaseToken,
                    claim.AssetId,
                    claim.AssetVersionId,
                    SourceRevision: 1,
                    canonical.ContentHash,
                    modelKey,
                    embeddingOptions.Provider,
                    embeddingOptions.Model,
                    embeddingOptions.Revision,
                    embeddingOptions.Digest,
                    embeddingOptions.Dimension,
                    embeddingOptions.ContentSchemaVersion,
                    new float[embeddingOptions.Dimension]);

                EmbeddingFinalizationStatus status = await finalizer.Finalize(validParams, CancellationToken.None);
                status.Should().Be(EmbeddingFinalizationStatus.COMMITTED);
            }
        }

        // 5. Cycle 3: duplicate enqueue prevented (0 enqueued, all assets already embedded)
        int enqueued3;
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, jobOptions);
            var fakeGen = new FakeEmbeddingGenerator(embeddingOptions, modelKey);
            var coord = new EmbeddingBackfillCoordinator(
                db, jobStore, fakeGen, MsOptions.Create(embeddingOptions), NullLogger<EmbeddingBackfillCoordinator>.Instance, TimeProvider.System, cacheService);

            enqueued3 = await coord.RunBackfillCycle(CancellationToken.None);
        }
        enqueued3.Should().Be(0, "Subsequent backfill cycle must not re-enqueue already embedded assets.");
    }

    private sealed class FakeEmbeddingGenerator(EmbeddingOptions options, string modelKey) : ITextEmbeddingGenerator
    {
        public Task<ModelVerificationResult> CheckModelAvailability(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelVerificationResult(true, null, options.Digest));

        public Task<GeneratedEmbedding> Generate(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbedding(new float[options.Dimension], modelKey, options.Dimension));
    }
}
