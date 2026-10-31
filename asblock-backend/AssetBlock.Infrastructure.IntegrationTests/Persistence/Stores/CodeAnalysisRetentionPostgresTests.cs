using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.HostedServices;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class CodeAnalysisRetentionPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RunCleanup_WhenReportPassedTwelveMonthClock_ShouldDeleteDetailsAndKeepRecentDetails()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset oldAsset = TestData.CreateAsset(author.Id, category.Id, title: "Old analysis");
        AssetVersion oldVersion = TestData.CreateAssetVersion(oldAsset.Id, isCurrent: true,
            processingStatus: AssetVersionProcessingStatus.READY);
        Asset recentAsset = TestData.CreateAsset(author.Id, category.Id, title: "Recent analysis");
        AssetVersion recentVersion = TestData.CreateAssetVersion(recentAsset.Id, isCurrent: true,
            processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.AddRange(oldAsset, recentAsset);
        db.AssetVersions.AddRange(oldVersion, recentVersion);
        await db.SaveChangesAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        CodeAnalysisReportHeader oldReport = AddReport(oldAsset, oldVersion, now.AddMonths(-12).AddSeconds(-1));
        CodeAnalysisReportHeader recentReport = AddReport(recentAsset, recentVersion, now.AddMonths(-12).AddSeconds(1));
        db.CodeAnalysisReportHeaders.AddRange(oldReport, recentReport);
        db.CodeAnalysisReportFindings.AddRange(AddFinding(oldReport.Id), AddFinding(recentReport.Id));
        await db.SaveChangesAsync();

        ServiceCollection services = new();
        services.AddSingleton(db);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        CodeAnalysisRetentionWorker worker = new(provider.GetRequiredService<IServiceScopeFactory>(), environment,
            NullLogger<CodeAnalysisRetentionWorker>.Instance, new FixedTimeProvider(now));

        var deleted = await worker.RunCleanup(CancellationToken.None);

        deleted.Should().Be(1);
        (await db.CodeAnalysisReportFindings.AnyAsync(f => f.ReportId == oldReport.Id)).Should().BeFalse();
        (await db.CodeAnalysisReportFindings.AnyAsync(f => f.ReportId == recentReport.Id)).Should().BeTrue();
        (await db.CodeAnalysisReportHeaders.AnyAsync(h => h.Id == oldReport.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task RunCleanup_WhenRejectedSubmissionLacksDecisionAndAppealClocks_ShouldRetainDetails()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Rejected material");
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, isCurrent: true,
            processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        CodeAnalysisReportHeader report = AddReport(asset, version, now.AddMonths(-13));
        AssetDraftWorkspace workspace = new()
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceVersionScopeKey = version.Id,
            ScopeId = Guid.NewGuid(),
            CreatedAt = now.AddMonths(-13),
            WorkspaceRevision = 1,
            CaseRevision = 1
        };
        ModerationSubmission rejectedSubmission = new()
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            OwnerUserId = author.Id,
            ContentSha256 = version.ContentSha256,
            DeclarationRevision = 1,
            MaterialMetadataRevision = 1,
            SellerEvidenceRevision = 1,
            PolicyVersion = "retention-test",
            SellerEvidenceDigest = new string('d', 64),
            State = ModerationSubmissionState.REJECTED,
            CaseRevision = 1,
            CreatedAt = now.AddMonths(-13)
        };
        db.CodeAnalysisReportHeaders.Add(report);
        db.CodeAnalysisReportFindings.Add(AddFinding(report.Id));
        db.AssetDraftWorkspaces.Add(workspace);
        db.ModerationSubmissions.Add(rejectedSubmission);
        await db.SaveChangesAsync();

        ServiceCollection services = new();
        services.AddSingleton(db);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        CodeAnalysisRetentionWorker worker = new(provider.GetRequiredService<IServiceScopeFactory>(), environment,
            NullLogger<CodeAnalysisRetentionWorker>.Instance, new FixedTimeProvider(now));

        var deleted = await worker.RunCleanup(CancellationToken.None);

        deleted.Should().Be(0);
        (await db.CodeAnalysisReportFindings.AnyAsync(f => f.ReportId == report.Id)).Should().BeTrue();
        (await db.ModerationDecisionRecords.AnyAsync(d => d.SubmissionId == rejectedSubmission.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task RunCleanup_WhenBuyerPurchaseProtectsHistory_ShouldRetainFindingDetails()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("analysis-retention-buyer", "analysis-retention-buyer@example.test");
        db.Users.Add(buyer);
        await db.SaveChangesAsync();
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Purchased history");
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, isCurrent: true,
            processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CodeAnalysisReportHeader report = AddReport(asset, version, now.AddMonths(-13));
        db.CodeAnalysisReportHeaders.Add(report);
        db.CodeAnalysisReportFindings.Add(AddFinding(report.Id));
        db.AssetArchiveAnalyses.Add(new AssetArchiveAnalysis
        {
            AssetVersionId = version.Id,
            FileCount = 3,
            TotalExpandedBytes = 120,
            ReadmeContent = "protected private README",
            ManifestMetadata = "{\"manifests\":[{\"packageName\":\"protected-private-manifest\"}]}",
            CreatedAt = now.AddMonths(-13)
        });
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, version.Id), asset.Title, author.Id);
        await db.SaveChangesAsync();
        await using ServiceProvider provider = CreateWorkerProvider(db, now);

        var deleted = await provider.GetRequiredService<CodeAnalysisRetentionWorker>().RunCleanup(CancellationToken.None);

        deleted.Should().Be(0);
        (await db.CodeAnalysisReportFindings.AnyAsync(f => f.ReportId == report.Id)).Should().BeTrue();
        (await db.Purchases.AnyAsync(p => p.AssetId == asset.Id)).Should().BeTrue();
        AssetArchiveAnalysis archive = await db.AssetArchiveAnalyses.AsNoTracking().SingleAsync(a => a.AssetVersionId == version.Id);
        archive.ReadmeContent.Should().Be("protected private README");
        archive.ManifestMetadata.Should().Contain("protected-private-manifest");
    }

    [Fact]
    public async Task RunCleanup_WhenApprovedPublicationProofExists_ShouldRetainFindingDetailsAndSnapshot()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Approved history");
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, isCurrent: true,
            processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CodeAnalysisReportHeader report = AddReport(asset, version, now.AddMonths(-13));
        db.CodeAnalysisReportHeaders.Add(report);
        db.CodeAnalysisReportFindings.Add(AddFinding(report.Id));
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        await using ServiceProvider provider = CreateWorkerProvider(db, now);

        var deleted = await provider.GetRequiredService<CodeAnalysisRetentionWorker>().RunCleanup(CancellationToken.None);

        deleted.Should().Be(0);
        (await db.CodeAnalysisReportFindings.AnyAsync(f => f.ReportId == report.Id)).Should().BeTrue();
        (await db.PublicationSnapshots.AnyAsync(s => s.AssetVersionId == version.Id)).Should().BeTrue();
        (await db.ModerationDecisionRecords.AnyAsync(d => d.AssetVersionId == version.Id
            && d.Outcome == ModerationSubmissionState.APPROVED)).Should().BeTrue();
    }

    [Fact]
    public async Task RunCleanup_WhenAssetIsLockedByConcurrentLifecycleWork_ShouldSkipUntilLockIsReleased()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Lifecycle lock");
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, isCurrent: true,
            processingStatus: AssetVersionProcessingStatus.READY);
        seedDb.Assets.Add(asset);
        seedDb.AssetVersions.Add(version);
        await seedDb.SaveChangesAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CodeAnalysisReportHeader report = AddReport(asset, version, now.AddMonths(-13));
        seedDb.CodeAnalysisReportHeaders.Add(report);
        AddFindingToContext(seedDb, report.Id);
        await seedDb.SaveChangesAsync();

        await using ApplicationDbContext lockDb = fixture.CreateDbContext();
        await using IDbContextTransaction tx = await lockDb.Database.BeginTransactionAsync();
        _ = await lockDb.Database.SqlQuery<Guid>($"""
            SELECT a."Id" AS "Value" FROM assets a WHERE a."Id" = {asset.Id} FOR UPDATE
            """).SingleAsync();
        await using ApplicationDbContext workerDb = fixture.CreateDbContext();
        await using ServiceProvider provider = CreateWorkerProvider(workerDb, now);

        var skipped = await provider.GetRequiredService<CodeAnalysisRetentionWorker>().RunCleanup(CancellationToken.None);
        skipped.Should().Be(0);
        (await workerDb.CodeAnalysisReportFindings.AnyAsync(f => f.ReportId == report.Id)).Should().BeTrue();

        await tx.CommitAsync();
        var deleted = await provider.GetRequiredService<CodeAnalysisRetentionWorker>().RunCleanup(CancellationToken.None);
        deleted.Should().Be(1);
        (await workerDb.CodeAnalysisReportFindings.AnyAsync(f => f.ReportId == report.Id)).Should().BeFalse();
    }


    [Theory]
    [InlineData("superseded")]
    [InlineData("selected-superseded")]
    [InlineData("response")]
    [InlineData("disposition")]
    public async Task RunCleanup_WhenReportIsNeeded_ShouldKeepAllSiblingsAndPurgeUnrelatedReport(string protection)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CodeAnalysisReportHeader report = AddReport(asset, version, now.AddMonths(-13));
        report.FindingCount = 2;
        CodeAnalysisReportHeader unrelated = AddReport(asset, version, now.AddMonths(-13));
        unrelated.FindingCount = 2;
        CodeAnalysisReportFinding answered = AddFinding(report.Id);
        db.CodeAnalysisReportHeaders.AddRange(report, unrelated);
        db.CodeAnalysisReportFindings.AddRange(answered, AddFinding(report.Id), AddFinding(unrelated.Id), AddFinding(unrelated.Id));
        var workspace = new AssetDraftWorkspace
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceVersionScopeKey = version.Id,
            ScopeId = Guid.NewGuid(),
            CreatedAt = now
        };
        db.AssetDraftWorkspaces.Add(workspace);
        if (protection is "superseded" or "selected-superseded" or "disposition")
        {
            var submission = new ModerationSubmission
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                AssetVersionId = version.Id,
                WorkspaceId = workspace.Id,
                WorkspaceVersionScopeKey = version.Id,
                OwnerUserId = author.Id,
                CodeAnalysisReportHeaderId = protection == "selected-superseded" ? null : report.Id,
                ContentSha256 = version.ContentSha256,
                DeclarationRevision = 1,
                MaterialMetadataRevision = 1,
                SellerEvidenceRevision = 0,
                SellerEvidenceDigest = new string('a', 64),
                PolicyVersion = "retention-test",
                CaseRevision = 1,
                State = protection == "disposition" ? ModerationSubmissionState.WITHDRAWN : ModerationSubmissionState.SUPERSEDED,
                CreatedAt = now
            };
            db.ModerationSubmissions.Add(submission);
            if (protection == "disposition")
            {
                db.ModerationFindingDispositions.Add(new ModerationFindingDisposition
                {
                    Id = Guid.NewGuid(),
                    AssetId = asset.Id,
                    AssetVersionId = version.Id,
                    SubmissionId = submission.Id,
                    ReportId = report.Id,
                    FindingId = answered.Id,
                    ModeratorUserId = author.Id,
                    CaseRevision = 1,
                    Disposition = FindingDispositionType.EXPLAINED,
                    Rationale = "Retention evidence",
                    CreatedAt = now
                });
            }
        }
        if (protection == "response")
        {
            var responseId = Guid.NewGuid();
            workspace.SellerEvidenceHeadRevision = 1;
            db.AssetSellerEvidenceRevisions.Add(new AssetSellerEvidenceRevision
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                AssetVersionId = version.Id,
                WorkspaceId = workspace.Id,
                WorkspaceVersionScopeKey = version.Id,
                Revision = 1,
                AuthorUserId = author.Id,
                SchemaVersion = 1,
                ContentDigest = new string('b', 64),
                PayloadJson = "{}",
                CreatedAt = now
            });
            db.SellerResponses.Add(new SellerResponse
            {
                Id = responseId,
                AssetId = asset.Id,
                AssetVersionId = version.Id,
                WorkspaceId = workspace.Id,
                WorkspaceVersionScopeKey = version.Id,
                ReportId = report.Id,
                FindingId = answered.Id,
                SellerEvidenceRevision = 1,
                ResponseType = SellerResponseType.NO_EVIDENCE,
                Text = "",
                ActorUserId = author.Id,
                CreatedAt = now
            });
        }
        await db.SaveChangesAsync();
        if (protection == "selected-superseded")
        {
            await db.AssetVersions.Where(v => v.Id == version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SelectedCodeAnalysisReportId, report.Id));
        }
        await using ServiceProvider provider = CreateWorkerProvider(db, now);
        (await provider.GetRequiredService<CodeAnalysisRetentionWorker>().RunCleanup(CancellationToken.None)).Should().Be(2);
        (await db.CodeAnalysisReportFindings.CountAsync(f => f.ReportId == report.Id)).Should().Be(2);
        (await db.CodeAnalysisReportFindings.AnyAsync(f => f.ReportId == unrelated.Id)).Should().BeFalse();
    }

    private static void AddFindingToContext(ApplicationDbContext db, Guid reportId) => db.CodeAnalysisReportFindings.Add(AddFinding(reportId));

    private static ServiceProvider CreateWorkerProvider(ApplicationDbContext db, DateTimeOffset now)
    {
        ServiceCollection services = new();
        services.AddSingleton(db);
        services.AddSingleton(Substitute.For<IHostEnvironment>());
        FixedTimeProvider timeProvider = new(now);
        services.AddSingleton<ILogger<CodeAnalysisRetentionWorker>>(NullLogger<CodeAnalysisRetentionWorker>.Instance);
        services.AddSingleton(sp => new CodeAnalysisRetentionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<ILogger<CodeAnalysisRetentionWorker>>(), timeProvider));
        return services.BuildServiceProvider();
    }

    private static CodeAnalysisReportHeader AddReport(Asset asset, AssetVersion version, DateTimeOffset finalizedAt) => new()
    {
        Id = Guid.NewGuid(),
        AssetId = asset.Id,
        AssetVersionId = version.Id,
        ContentSha256 = version.ContentSha256,
        PolicyVersion = "retention-test",
        InputRevision = 1,
        Purpose = CodeAnalysisReportPurpose.PRODUCTION,
        ReviewEligible = false,
        IsFinalized = true,
        FinalizedAt = finalizedAt,
        ReportSchemaVersion = 1,
        FindingCount = 1,
        CreatedAt = finalizedAt
    };

    private static CodeAnalysisReportFinding AddFinding(Guid reportId) => new()
    {
        Id = Guid.NewGuid(),
        ReportId = reportId,
        FindingKey = Guid.NewGuid().ToString("N"),
        FindingType = "POTENTIAL_MATCH",
        OwnScopePath = "src/main.cs",
        OwnExcerpt = "bounded excerpt",
        ForeignSensitivity = CodeAnalysisFindingSensitivity.PRIVATE,
        ReasonText = "retention test detail",
        RequestedAction = "review",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
