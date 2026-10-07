using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public class ModerationFoundationStoreCreateAuditPostgresTests(PostgresFixture fixture)
{
    private static ModerationFoundationStore CreateStore(ApplicationDbContext db, IAuditWriter? auditWriter = null) =>
        new(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db), auditWriter: auditWriter);

    private static async Task<(User Author, Category Category)> SeedAuthorAsync(ApplicationDbContext db)
    {
        User author = TestData.CreateUser();
        Category category = TestData.CreateCategory();
        db.Users.Add(author);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return (author, category);
    }

    private static CreateAssetDraftRequest CreateRequest(Guid ownerId, Guid categoryId, Guid operationId, string title = "Title") =>
        new(ownerId, operationId, title, "desc", categoryId, 10m, null);

    [Fact]
    public async Task CreateAssetDraft_WhenConcurrentIdenticalRequestsRace_ShouldReplayTheCommittedDraft()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await SeedAuthorAsync(db);
        var operationId = Guid.NewGuid();

        Task<SellerDraftCreatedDto?> firstTask = Task.Run(async () =>
        {
            await using ApplicationDbContext firstDb = fixture.CreateDbContext();
            return await CreateStore(firstDb).CreateAssetDraft(
                CreateRequest(author.Id, category.Id, operationId), CancellationToken.None);
        });
        Task<SellerDraftCreatedDto?> secondTask = Task.Run(async () =>
        {
            await using ApplicationDbContext secondDb = fixture.CreateDbContext();
            return await CreateStore(secondDb).CreateAssetDraft(
                CreateRequest(author.Id, category.Id, operationId), CancellationToken.None);
        });

        await Task.WhenAll(firstTask, secondTask);
        SellerDraftCreatedDto first = (await firstTask)!;
        SellerDraftCreatedDto second = (await secondTask)!;

        // The advisory lock serializes creation: one commits, the other replays the same resource.
        second.AssetId.Should().Be(first.AssetId);
        second.WorkspaceId.Should().Be(first.WorkspaceId);
        new[] { first.Replayed, second.Replayed }.Count(replayed => replayed).Should().Be(1);
        (await db.Assets.CountAsync(a => a.Id == first.AssetId)).Should().Be(1);
        (await db.JsonMutationIdempotencyRecords.CountAsync(r => r.OperationId == operationId)).Should().Be(1);
    }

    [Fact]
    public async Task CreateAssetDraft_WhenSameOperationIdHasChangedPayload_ShouldConflict()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await SeedAuthorAsync(db);
        ModerationFoundationStore store = CreateStore(db);
        var operationId = Guid.NewGuid();

        (await store.CreateAssetDraft(CreateRequest(author.Id, category.Id, operationId))).Should().NotBeNull();
        (await store.CreateAssetDraft(CreateRequest(author.Id, category.Id, operationId, title: "Changed"))).Should().BeNull();
    }

    [Fact]
    public async Task CreateAssetDraft_ShouldSeedInitialMaterialHeadThatSnapshotsAndAttachCarryForward()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await SeedAuthorAsync(db);
        ModerationFoundationStore store = CreateStore(db);
        var operationId = Guid.NewGuid();

        SellerDraftCreatedDto first = (await store.CreateAssetDraft(
            CreateRequest(author.Id, category.Id, operationId, title: "Draft title")))!;

        // The created draft exposes its initial metadata without any extra material save.
        SellerDraftSnapshotDto? snapshot = await store.GetOwnerDraftSnapshotByWorkspace(first.WorkspaceId, author.Id);
        snapshot.Should().NotBeNull();
        snapshot.Material.Title.Should().Be("Draft title");
        snapshot.Material.CategoryId.Should().Be(category.Id);
        snapshot.Material.Tags.Should().BeEmpty();

        // Uploading bytes copies the initial material head into the version workspace.
        AssetVersion version = TestData.CreateAssetVersion(first.AssetId);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        VersionAttachResult attach = await store.AttachUploadedVersionToWorkspace(
            first.AssetId, version.Id, author.Id, first.WorkspaceId, first.WorkspaceRevision);

        attach.Status.Should().Be(VersionAttachStatus.ATTACHED);
        attach.MaterialHeadRevision.Should().Be(1);
        List<AssetMaterialMetadataRevision> versionRevisions = await db.AssetMaterialMetadataRevisions
            .Where(r => r.WorkspaceId == attach.VersionWorkspace!.WorkspaceId)
            .ToListAsync();
        versionRevisions.Should().ContainSingle();
        versionRevisions[0].PayloadJson.Should().Contain("Draft title");
        versionRevisions[0].PayloadJson.Should().Contain(category.Id.ToString());
    }

    [Fact]
    public async Task CreateAssetDraft_WhenAuditEventProvided_ShouldCommitAuditWithMutationAndNotReplayIt()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await SeedAuthorAsync(db);
        var auditWriter = new AuditWriter(
            new AuditStore(db), new NullAuditContextAccessor(), NullLogger<AuditWriter>.Instance);
        ModerationFoundationStore store = CreateStore(db, auditWriter);
        var operationId = Guid.NewGuid();

        SellerDraftCreatedDto first = (await store.CreateAssetDraft(
            CreateRequest(author.Id, category.Id, operationId) with
            {
                AuditEvent = new AuditEvent(AuditActions.ASSET_DRAFT_CREATE, AuditOutcome.SUCCESS, AuditResourceTypes.ASSET)
            }))!;

        List<AuditLog> entries = await db.AuditLogs.Where(a => a.ResourceId == first.AssetId.ToString()).ToListAsync();
        entries.Should().ContainSingle();

        await store.CreateAssetDraft(CreateRequest(author.Id, category.Id, operationId));
        (await db.AuditLogs.CountAsync(a => a.ResourceId == first.AssetId.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task SaveDraftRevision_WhenTrackedWorkspaceIsStale_ShouldRecheckFreshUnderLock()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsyncShared(db);
        ModerationFoundationStore store = CreateStore(db);
        AssetDraftWorkspaceSnapshot workspace = await store.EnsurePreUploadWorkspace(asset.Id, author.Id);

        // Another transaction moves the workspace forward; the tracked instance in `db`
        // still holds the pre-lock revision and must not satisfy the CAS.
        await using ApplicationDbContext bumpDb = fixture.CreateDbContext();
        await bumpDb.Database.ExecuteSqlAsync(
            $"UPDATE asset_draft_workspaces SET \"WorkspaceRevision\" = \"WorkspaceRevision\" + 1 WHERE \"Id\" = {workspace.WorkspaceId}");

        var payloadJson = Application.Common.DraftPayloadJson.Serialize(
            new SellerDraftMaterialPayload("T", null, asset.CategoryId, []));
        var digest = Application.Common.DraftPayloadJson.ComputeDigest(payloadJson);

        ModerationDraftSaveResult result = await store.SaveDraftRevision(
            new DraftRevisionSaveRequest(
                author.Id, asset.Id, null, ModerationOperationKinds.DRAFT_SAVE,
                Guid.NewGuid(), digest, payloadJson,
                SellerDraftLimits.MATERIAL_METADATA_SCHEMA_VERSION, digest,
                workspace.WorkspaceRevision),
            CancellationToken.None);

        result.Status.Should().Be(ModerationDraftSaveStatus.STALE_WORKSPACE);
        (await db.AssetMaterialMetadataRevisions.CountAsync(r => r.AssetId == asset.Id)).Should().Be(0);
    }

    [Fact]
    public async Task WithdrawSubmission_WhenTrackedSubmissionIsStale_ShouldRecheckFreshUnderLock()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsyncShared(db);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        AssetDraftWorkspace workspace = new()
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceVersionScopeKey = version.Id,
            ScopeId = Guid.NewGuid(),
            WorkspaceRevision = 1,
            CaseRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        ModerationSubmission submission = new()
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = workspace.WorkspaceVersionScopeKey,
            OwnerUserId = author.Id,
            ContentSha256 = version.ContentSha256,
            DeclarationRevision = 1,
            MaterialMetadataRevision = 1,
            SellerEvidenceRevision = 1,
            PolicyVersion = "policy-v1",
            SellerEvidenceDigest = new string('d', 64),
            State = ModerationSubmissionState.SUBMITTED,
            CaseRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AssetVersions.Add(version);
        db.AssetDraftWorkspaces.Add(workspace);
        db.ModerationSubmissions.Add(submission);
        await db.SaveChangesAsync();

        // Another transaction advances the case revision; the tracked submission in `db`
        // still holds the pre-lock value and must not satisfy the CAS.
        await using ApplicationDbContext bumpDb = fixture.CreateDbContext();
        await bumpDb.Database.ExecuteSqlAsync(
            $"UPDATE moderation_submissions SET \"CaseRevision\" = \"CaseRevision\" + 1 WHERE \"Id\" = {submission.Id}");

        ModerationFoundationStore store = CreateStore(db);
        var requestDigest = Application.Common.DraftPayloadJson.ComputeDigest(
            Application.Common.DraftPayloadJson.Serialize(new { submission.Id, ExpectedCaseRevision = 1L }));
        WithdrawSubmissionResult result = await store.WithdrawSubmission(
            new WithdrawSubmissionRequest(author.Id, submission.Id, 1, Guid.NewGuid(), requestDigest, ModerationWithdrawalReason.SELLER_WITHDRAWAL),
            CancellationToken.None);

        result.Status.Should().Be(WithdrawSubmissionStatus.STALE_CASE);
        ModerationSubmission reloaded = await db.ModerationSubmissions.FirstAsync(s => s.Id == submission.Id);
        reloaded.State.Should().Be(ModerationSubmissionState.SUBMITTED);
    }

    private static async Task<(User Author, Category Category, Asset Asset)> SeedAssetAsyncShared(ApplicationDbContext db)
    {
        User author = TestData.CreateUser();
        Category category = TestData.CreateCategory();
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Users.Add(author);
        db.Categories.Add(category);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return (author, category, asset);
    }
}
