using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Publication;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Publication;
using Microsoft.EntityFrameworkCore;

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
    public async Task TrustedApprovedSnapshots_WhenAdmissionDeniesAndHeaderClaimsAuthority_ShouldExclude()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        await Seed(db, headerOnly: false, admission: false, admissionDeniesAuthority: true);

        List<PublicationSnapshot> trusted = await PublicationEligibilityQuery.TrustedApprovedSnapshots(db).ToListAsync();

        trusted.Should().BeEmpty();
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

        version.SafetyRestriction = AssetVersionSafetyRestriction.UNSAFE_ARCHIVE;
        PublicationEligibility.IsAuthorSafePreviewVersion(version).Should().BeFalse();
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
        bool admissionDeniesAuthority = false,
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
                report, chain, canAuthorizePublication: !admissionDeniesAuthority);

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
