using System.Data.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Domain.Core.Publication;
using AssetBlock.Infrastructure.HostedServices;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Publication;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Negative publication cases. A header is never trusted on its own: the trusted predicate needs a
/// completed analysis run for the exact asset, version, content hash and policy, plus a server
/// admission whose input digest matches that run. These tests sit next to the positive fixtures so
/// the guard cannot be relaxed without failing here.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class AdmittedReportTrustPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TrustedApprovedSnapshots_WhenOnlyFinalizedHeaderExists_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        await Seed(db, headerOnly: true, admission: false);

        List<PublicationSnapshot> trusted = await PublicationEligibilityQuery.TrustedApprovedSnapshots(db).ToListAsync();

        trusted.Should().BeEmpty();
    }

    [Fact]
    public async Task TrustedApprovedSnapshots_WhenReviewEligibilityIsFalseAndDecisionApproves_ShouldUseTheDecision()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        await Seed(db, headerOnly: false, admission: true, admissionReviewIneligible: true);

        List<PublicationSnapshot> trusted = await PublicationEligibilityQuery.TrustedApprovedSnapshots(db).ToListAsync();

        trusted.Should().ContainSingle();
    }

    [Fact]
    public async Task TrustedApprovedSnapshots_WhenReviewEligibleWithoutApprovedDecision_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        await Seed(db, headerOnly: false, admission: true);
        await db.ModerationDecisionRecords.ExecuteDeleteAsync();
        (await PublicationEligibilityQuery.TrustedApprovedSnapshots(db).ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TrustedApprovedSnapshots_WhenHeaderHasNoBoundRun_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        await Seed(db, headerOnly: false, admission: true, bindRunToHeader: false);

        List<PublicationSnapshot> trusted = await PublicationEligibilityQuery.TrustedApprovedSnapshots(db).ToListAsync();

        trusted.Should().BeEmpty();
    }

    [Fact]
    public async Task TrustedApprovedSnapshots_WhenAdmissionInputDigestDoesNotMatchRun_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        await Seed(db, headerOnly: false, admission: true, admissionInputDigestMismatch: true);

        List<PublicationSnapshot> trusted = await PublicationEligibilityQuery.TrustedApprovedSnapshots(db).ToListAsync();

        trusted.Should().BeEmpty();
    }

    [Fact]
    public async Task TrustedApprovedSnapshots_WhenRunIsNotCompleted_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        await Seed(db, headerOnly: false, admission: true, runState: CodeAnalysisRunState.FAILED);

        List<PublicationSnapshot> trusted = await PublicationEligibilityQuery.TrustedApprovedSnapshots(db).ToListAsync();

        trusted.Should().BeEmpty();
    }

    [Fact]
    public async Task TrustedApprovedSnapshots_WhenHeaderContentHashDiffersFromVersion_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        await Seed(db, headerOnly: false, admission: true, headerContentHashMismatch: true);

        List<PublicationSnapshot> trusted = await PublicationEligibilityQuery.TrustedApprovedSnapshots(db).ToListAsync();

        trusted.Should().BeEmpty();
    }

    [Fact]
    public async Task TrustedApprovedSnapshots_WhenCoherentChainExists_ShouldInclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        await Seed(db, headerOnly: false, admission: true);

        List<PublicationSnapshot> trusted = await PublicationEligibilityQuery.TrustedApprovedSnapshots(db).ToListAsync();

        trusted.Should().ContainSingle();
    }

    [Fact]
    public async Task SaleEligibleSnapshot_WhenVersionIsQuarantined_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (Guid assetId, Guid versionId) = await Seed(db, headerOnly: false, admission: true);

        // An actual infected result quarantines this exact version without touching entitlement.
        await db.AssetVersions
            .Where(v => v.Id == versionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.SafetyRestriction, AssetVersionSafetyRestriction.INFECTED)
                .SetProperty(v => v.SafetyRestrictionReason, "MALWARE_DETECTED")
                .SetProperty(v => v.SafetyRestrictedAt, DateTimeOffset.UtcNow));

        List<PublicationSnapshot> saleEligible = await PublicationEligibilityQuery
            .SaleEligibleSnapshot(db, assetId, versionId)
            .ToListAsync();

        saleEligible.Should().BeEmpty();
    }

    [Fact]
    public async Task BuyerAccessibleVersions_WhenVersionIsQuarantined_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (Guid assetId, Guid versionId) = await Seed(db, headerOnly: false, admission: true);

        await db.AssetVersions
            .Where(v => v.Id == versionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.SafetyRestriction, AssetVersionSafetyRestriction.INTEGRITY_MISMATCH)
                .SetProperty(v => v.SafetyRestrictionReason, "ARCHIVE_CORRUPT")
                .SetProperty(v => v.SafetyRestrictedAt, DateTimeOffset.UtcNow));

        List<AssetVersion> buyerVersions = await PublicationEligibilityQuery
            .BuyerAccessibleVersions(db, assetId, 1)
            .ToListAsync();

        buyerVersions.Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerSafePreview_WhenVersionIsQuarantined_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (Guid _, Guid versionId) = await Seed(db, headerOnly: false, admission: true);
        AssetVersion version = await db.AssetVersions.FirstAsync(v => v.Id == versionId);

        PublicationEligibility.IsAuthorSafePreviewVersion(version).Should().BeTrue();

        version.StorageRetiredAt = DateTimeOffset.UtcNow;
        PublicationEligibility.IsAuthorSafePreviewVersion(version).Should().BeFalse();
        version.StorageRetiredAt = null;

        version.SafetyRestriction = AssetVersionSafetyRestriction.UNSAFE_ARCHIVE;
        PublicationEligibility.IsAuthorSafePreviewVersion(version).Should().BeFalse();
    }

    [Fact]
    public async Task OwnerFindings_WhenAdmissionInputDigestDoesNotMatchCompletedRun_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (Guid assetId, Guid versionId) = await Seed(db, headerOnly: false, admission: true, admissionInputDigestMismatch: true);
        Guid ownerId = await db.Assets.AsNoTracking().Where(a => a.Id == assetId).Select(a => a.AuthorId).SingleAsync();
        Guid reportId = await db.CodeAnalysisReportHeaders.AsNoTracking().Where(h => h.AssetVersionId == versionId)
            .Select(h => h.Id).SingleAsync();
        CodeAnalysisSellerStore store = CreateSellerStore(db);

        SellerAnalysisFindingsPageDto? findings = await store.GetOwnerFindings(versionId, reportId, ownerId, 1);

        findings.Should().BeNull();
    }

    [Fact]
    public async Task OwnerFindings_WhenBoundRunIsFailed_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (Guid assetId, Guid versionId) = await Seed(db, headerOnly: false, admission: true, runState: CodeAnalysisRunState.FAILED);
        Guid ownerId = await db.Assets.AsNoTracking().Where(a => a.Id == assetId).Select(a => a.AuthorId).SingleAsync();
        Guid reportId = await db.CodeAnalysisReportHeaders.AsNoTracking().Where(h => h.AssetVersionId == versionId)
            .Select(h => h.Id).SingleAsync();
        CodeAnalysisSellerStore store = CreateSellerStore(db);

        SellerAnalysisFindingsPageDto? findings = await store.GetOwnerFindings(versionId, reportId, ownerId, 1);

        findings.Should().BeNull();
    }

    [Fact]
    public async Task OwnerFindings_WhenHistoricalAdmissionChainIsCoherent_ShouldRemainReadableAndNotCurrent()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (Guid assetId, Guid versionId) = await Seed(db, headerOnly: false, admission: true);
        Guid reportId = await db.CodeAnalysisReportHeaders.AsNoTracking().Where(h => h.AssetVersionId == versionId)
            .Select(h => h.Id).SingleAsync();
        await db.CodeAnalysisReportAdmissions.Where(a => a.ReportId == reportId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.AdmittedBy, "assetblock-dotnet-finalizer")
                .SetProperty(a => a.ReviewEligible, false));
        await db.CodeAnalysisReportHeaders.Where(h => h.Id == reportId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.ReviewEligible, false));
        Guid ownerId = await db.Assets.AsNoTracking().Where(a => a.Id == assetId).Select(a => a.AuthorId).SingleAsync();
        CodeAnalysisSellerStore store = CreateSellerStore(db);

        SellerAnalysisFindingsPageDto? findings = await store.GetOwnerFindings(versionId, reportId, ownerId, 1);

        findings.Should().NotBeNull();
        findings.IsCurrent.Should().BeFalse();
    }

    [Fact]
    public async Task OwnerFindings_WhenExpiredDetailsAreMissing_ShouldRetainSummaryAndMarkDetailsUnavailable()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (Guid assetId, Guid versionId) = await Seed(db, headerOnly: false, admission: true);
        Guid reportId = await db.CodeAnalysisReportHeaders.AsNoTracking().Where(h => h.AssetVersionId == versionId)
            .Select(h => h.Id).SingleAsync();
        await db.CodeAnalysisReportAdmissions.Where(a => a.ReportId == reportId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.AdmittedBy, "assetblock-dotnet-finalizer")
                .SetProperty(a => a.ReviewEligible, false));
        await db.CodeAnalysisReportHeaders.Where(h => h.Id == reportId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.ReviewEligible, false)
                .SetProperty(h => h.FindingCount, 3));
        Guid ownerId = await db.Assets.AsNoTracking().Where(a => a.Id == assetId).Select(a => a.AuthorId).SingleAsync();
        CodeAnalysisSellerStore store = CreateSellerStore(db);

        SellerAnalysisFindingsPageDto? findings = await store.GetOwnerFindings(versionId, reportId, ownerId, 1);
        SellerAnalysisSummaryDto? summary = await store.GetOwnerSummary(versionId, ownerId);

        findings.Should().NotBeNull();
        findings.DetailsAvailable.Should().BeFalse();
        findings.Items.Should().BeEmpty();
        findings.TotalCount.Should().Be(3);
        summary.Should().NotBeNull();
        summary.FindingCount.Should().Be(3);
        summary.FindingDetailsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task OwnerFindings_WhenRetentionCommitsAfterCount_ShouldReturnCoherentPrePurgeSnapshot()
    {
        FindingsCountBarrier barrier = new();
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext(options => options.AddInterceptors(barrier));
        (Guid assetId, Guid versionId) = await Seed(db, headerOnly: false, admission: true);
        Guid reportId = await db.CodeAnalysisReportHeaders.AsNoTracking().Where(h => h.AssetVersionId == versionId)
            .Select(h => h.Id).SingleAsync();
        db.CodeAnalysisReportFindings.Add(new CodeAnalysisReportFinding
        {
            Id = Guid.NewGuid(),
            ReportId = reportId,
            FindingKey = Guid.NewGuid().ToString("N"),
            FindingType = "POTENTIAL_MATCH",
            OwnScopePath = "src/retention-race.cs",
            OwnExcerpt = "bounded finding excerpt",
            ForeignSensitivity = CodeAnalysisFindingSensitivity.PRIVATE,
            ReasonText = "retention concurrency regression",
            RequestedAction = "review",
            CreatedAt = DateTimeOffset.UtcNow.AddMonths(-13)
        });
        await db.SaveChangesAsync();
        await db.CodeAnalysisReportAdmissions.Where(a => a.ReportId == reportId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.AdmittedBy, "assetblock-dotnet-finalizer")
                .SetProperty(a => a.ReviewEligible, false));
        await db.CodeAnalysisReportHeaders.Where(h => h.Id == reportId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.ReviewEligible, false)
                .SetProperty(h => h.FindingCount, 1)
                .SetProperty(h => h.FinalizedAt, DateTimeOffset.UtcNow.AddMonths(-13)));
        await db.ModerationDecisionRecords.Where(d => d.AssetVersionId == versionId).ExecuteDeleteAsync();
        await db.PublicationSnapshots.Where(s => s.AssetVersionId == versionId).ExecuteDeleteAsync();
        await db.ModerationSubmissions.Where(s => s.AssetVersionId == versionId).ExecuteDeleteAsync();
        Guid ownerId = await db.Assets.AsNoTracking().Where(a => a.Id == assetId).Select(a => a.AuthorId).SingleAsync();
        CodeAnalysisSellerStore store = CreateSellerStore(db);
        Task<SellerAnalysisFindingsPageDto?> read = store.GetOwnerFindings(versionId, reportId, ownerId, 1);

        await barrier.CountReadObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using ApplicationDbContext workerDb = fixture.CreateDbContext();
        await using ServiceProvider provider = CreateRetentionWorkerProvider(workerDb, DateTimeOffset.UtcNow);
        (await workerDb.CodeAnalysisReportHeaders.AsNoTracking().Where(h => h.Id == reportId)
            .Select(h => h.FinalizedAt).SingleAsync()).Should().BeBefore(DateTimeOffset.UtcNow.AddMonths(-12));
        (await workerDb.ModerationSubmissions.AnyAsync(s => s.AssetVersionId == versionId)).Should().BeFalse();
        (await workerDb.PublicationSnapshots.AnyAsync(s => s.AssetVersionId == versionId)).Should().BeFalse();
        (await workerDb.Purchases.AnyAsync(p => p.AssetId == assetId)).Should().BeFalse();
        (await workerDb.CheckoutReservations.AnyAsync(r => r.AssetId == assetId)).Should().BeFalse();
        (await workerDb.CheckoutIntentItems.AnyAsync(i => i.AssetId == assetId)).Should().BeFalse();
        (await workerDb.PaidCheckoutReconciliationHolds.AnyAsync(h => h.CheckoutIntent.Items.Any(i => i.AssetId == assetId))).Should().BeFalse();
        (await workerDb.CodeAnalysisRuns.AnyAsync(r => r.AssetVersionId == versionId
            && (r.State == CodeAnalysisRunState.QUEUED || r.State == CodeAnalysisRunState.RUNNING))).Should().BeFalse();
        (await workerDb.SecurityRechecks.AnyAsync(r => r.AssetVersionId == versionId
            && (r.State == SecurityRecheckState.QUEUED || r.State == SecurityRecheckState.INSPECTING
                || r.State == SecurityRecheckState.SCANNING))).Should().BeFalse();
        int purged;
        try
        {
            purged = await provider.GetRequiredService<CodeAnalysisRetentionWorker>().RunCleanup(CancellationToken.None);
        }
        finally
        {
            barrier.ReleaseCountRead.TrySetResult(true);
        }
        purged.Should().Be(1);

        SellerAnalysisFindingsPageDto? page = await read.WaitAsync(TimeSpan.FromSeconds(10));
        page.Should().NotBeNull();
        page.DetailsAvailable.Should().BeTrue();
        page.Items.Should().ContainSingle();
        page.TotalCount.Should().Be(1);
        (await workerDb.CodeAnalysisReportFindings.AnyAsync(f => f.ReportId == reportId)).Should().BeFalse();
    }

    [Fact]
    public async Task OwnerFindings_WhenRequestedByAnotherExistingOwner_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (Guid assetId, Guid versionId) = await Seed(db, headerOnly: false, admission: true, admissionReviewIneligible: true);
        var suffix = Guid.NewGuid().ToString("N");
        User foreignOwner = new()
        {
            Id = Guid.NewGuid(),
            Username = $"foreign_{suffix}",
            Email = $"foreign-{suffix}@test.local",
            PasswordHash = "integration-test-hash",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(foreignOwner);
        await db.SaveChangesAsync();
        Guid reportId = await db.CodeAnalysisReportHeaders.AsNoTracking().Where(h => h.AssetVersionId == versionId)
            .Select(h => h.Id).SingleAsync();
        await db.CodeAnalysisReportAdmissions.Where(a => a.ReportId == reportId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AdmittedBy, "assetblock-dotnet-finalizer"));
        CodeAnalysisSellerStore store = CreateSellerStore(db);

        SellerAnalysisFindingsPageDto? findings = await store.GetOwnerFindings(versionId, reportId, foreignOwner.Id, 1);

        findings.Should().BeNull();
        (await db.Assets.AsNoTracking().AnyAsync(a => a.Id == assetId && a.AuthorId != foreignOwner.Id)).Should().BeTrue();
    }

    private static CodeAnalysisSellerStore CreateSellerStore(ApplicationDbContext db)
    {
        CodeAnalysisPolicyVersion policy = db.CodeAnalysisPolicyVersions.AsNoTracking().Single();
        return new CodeAnalysisSellerStore(
            db,
            Substitute.For<IAnalysisRunEnqueuer>(),
            Substitute.For<ISecurityProvenanceStore>(),
            Substitute.For<IAuditWriter>(),
            TimeProvider.System,
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = "code-analysis",
                PolicyKey = policy.PolicyKey,
                PolicyDigest = policy.PolicyDigest
            }));
    }

    private static ServiceProvider CreateRetentionWorkerProvider(ApplicationDbContext db, DateTimeOffset now)
    {
        ServiceCollection services = new();
        services.AddSingleton(db);
        services.AddSingleton(Substitute.For<IHostEnvironment>());
        services.AddSingleton<ILogger<CodeAnalysisRetentionWorker>>(NullLogger<CodeAnalysisRetentionWorker>.Instance);
        services.AddSingleton(sp => new CodeAnalysisRetentionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<ILogger<CodeAnalysisRetentionWorker>>(), new FixedTimeProvider(now)));
        return services.BuildServiceProvider();
    }

    private sealed class FindingsCountBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> CountReadObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseCountRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("code_analysis_report_findings", StringComparison.OrdinalIgnoreCase))
            {
                CountReadObserved.TrySetResult(true);
                await ReleaseCountRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Seeds an approved submission, decision and snapshot over a deliberately incomplete or coherent
    /// report evidence chain, so the trusted predicate is what decides the outcome.
    /// </summary>
    private static async Task<(Guid AssetId, Guid VersionId)> Seed(
        ApplicationDbContext db,
        bool headerOnly,
        bool admission,
        bool bindRunToHeader = true,
        bool admissionInputDigestMismatch = false,
        bool admissionReviewIneligible = false,
        bool headerContentHashMismatch = false,
        CodeAnalysisRunState runState = CodeAnalysisRunState.COMPLETED)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Trust subject");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var workspace = new AssetDraftWorkspace
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceVersionScopeKey = version.Id,
            ScopeId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            WorkspaceRevision = 1,
            CaseRevision = 1
        };
        db.AssetDraftWorkspaces.Add(workspace);

        var submission = new ModerationSubmission
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
        db.ModerationSubmissions.Add(submission);

        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        CodeAnalysisReportHeader report = AdmittedReportTestEvidence.Header(
            version, chain,
            // A header with no chain has no run to bind to, so it must not carry a dangling id.
            omitRunBinding: !bindRunToHeader || headerOnly,
            contentHashMismatch: headerContentHashMismatch);

        if (!headerOnly)
        {
            db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
            await AdmittedReportTestEvidence.SeedObservedProofAsync(db, version, chain);
            db.CodeAnalysisRuns.Add(AdmittedReportTestEvidence.CompletedRun(version, chain, runState));
        }

        db.CodeAnalysisReportHeaders.Add(report);

        if (admission)
        {
            CodeAnalysisReportAdmission record = AdmittedReportTestEvidence.Admission(
                report, chain, reviewEligible: !admissionReviewIneligible);

            if (admissionInputDigestMismatch)
            {
                record.InputDigest = AdmittedReportTestEvidence.NewHex64();
            }

            db.CodeAnalysisReportAdmissions.Add(record);
        }

        var snapshot = new PublicationSnapshot
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            CodeAnalysisReportHeaderId = report.Id,
            ModerationSubmissionId = submission.Id,
            PolicyVersion = "policy-v1",
            ApprovedMetadataJson = ApprovedPublicationMetadata.BuildJson(asset.Title, asset.Description, category.Id, []),
            RightsReferenceJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PublicationSnapshots.Add(snapshot);

        db.ModerationDecisionRecords.Add(new ModerationDecisionRecord
        {
            Id = Guid.NewGuid(),
            SubmissionId = submission.Id,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ModeratorUserId = author.Id,
            Outcome = ModerationSubmissionState.APPROVED,
            PublicationSnapshotId = snapshot.Id,
            CaseRevision = 1,
            Message = "negative case fixture",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        return (asset.Id, version.Id);
    }
}
