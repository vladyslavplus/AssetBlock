using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AssetProcessingLifecycleStorePostgresTests(PostgresFixture fixture)
{
    private static readonly TimeSpan _leaseDuration = TimeSpan.FromMinutes(1);

    private static AssetProcessingJobStore CreateJobStore(ApplicationDbContext db) =>
        new(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));

    private static AssetProcessingLifecycleStore CreateLifecycleStore(ApplicationDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));

    private async Task<TestContext> SetupRunningJob(AssetProcessingJobType jobType, TimeSpan? leaseDuration = null)
    {
        ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id,
            isCurrent: false,
            processingStatus: jobType == AssetProcessingJobType.ARCHIVE_INSPECTION
                ? AssetVersionProcessingStatus.PENDING_INSPECTION
                : AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        AssetProcessingPayload payload = jobType == AssetProcessingJobType.ARCHIVE_INSPECTION
            ? new ArchiveInspectionPayload()
            : new MalwareScanPayload("1.0");

        await jobStore.Enqueue(
            asset.Id,
            version.Id,
            jobType,
            definitionVersion: 1,
            initialDelay: TimeSpan.Zero,
            payload: payload);

        IReadOnlyList<ClaimedAssetProcessingJob> claimed = await jobStore.ClaimPendingBatch(
            batchSize: 1,
            leaseDuration: leaseDuration ?? _leaseDuration,
            leaseOwner: "test-worker-1");

        claimed.Should().HaveCount(1);
        ClaimedAssetProcessingJob claimedJob = claimed[0];

        AssetProcessingLifecycleStore lifecycleStore = CreateLifecycleStore(db);
        return new TestContext(db, author, asset, version, jobStore, lifecycleStore, claimedJob);
    }

    private sealed record TestContext(
        ApplicationDbContext Db,
        User Author,
        Asset Asset,
        AssetVersion Version,
        AssetProcessingJobStore JobStore,
        AssetProcessingLifecycleStore LifecycleStore,
        ClaimedAssetProcessingJob ClaimedJob);

    [Fact]
    public async Task TransitionArchiveInspectionAccepted_WithValidToken_ShouldPersistAnalysisAndEnqueueMalwareScan()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);
        var manifest = new ArchiveAnalysisManifestMetadata([
            new RecognizedManifestItem("package.json", "npm", "@pkg/core", "1.0.0")
        ]);

        var analysis = new BoundedArchiveAnalysisRecord(
            FileCount: 5,
            TotalExpandedBytes: 1024,
            ReadmeContent: "# Readme",
            ManifestMetadata: manifest);

        var result = new ArchiveInspectionResult(
            FileCount: 5,
            TotalSizeUncompressed: 1024);

        var success = await ctx.LifecycleStore.TransitionArchiveInspectionAccepted(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            result,
            analysis);

        success.Should().BeTrue();

        // Verify version updated to PENDING_MALWARE_SCAN
        AssetVersion updatedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        updatedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        updatedVersion.ProcessingErrorCode.Should().BeNull();
        updatedVersion.ProcessingErrorSummary.Should().BeNull();

        // Verify analysis row saved
        AssetArchiveAnalysis savedAnalysis = await ctx.Db.AssetArchiveAnalyses.AsNoTracking().FirstAsync(a => a.AssetVersionId == ctx.Version.Id);
        savedAnalysis.FileCount.Should().Be(5);
        savedAnalysis.TotalExpandedBytes.Should().Be(1024);
        savedAnalysis.ReadmeContent.Should().Be("# Readme");

        // Verify ARCHIVE_INSPECTION job is SUCCEEDED
        AssetProcessingJob archiveJob = await ctx.Db.AssetProcessingJobs.AsNoTracking().FirstAsync(j => j.Id == ctx.ClaimedJob.JobId);
        archiveJob.Status.Should().Be(AssetProcessingJobStatus.SUCCEEDED);

        // Verify exactly one MALWARE_SCAN job enqueued
        List<AssetProcessingJob> malwareJobs = await ctx.Db.AssetProcessingJobs.AsNoTracking()
            .Where(j => j.AssetVersionId == ctx.Version.Id && j.Type == AssetProcessingJobType.MALWARE_SCAN)
            .ToListAsync();
        malwareJobs.Should().HaveCount(1);
        malwareJobs[0].Status.Should().Be(AssetProcessingJobStatus.QUEUED);
    }

    [Fact]
    public async Task TransitionArchiveInspectionAccepted_WithStaleOrExpiredToken_ShouldFailWithoutMutating()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);
        var staleToken = Guid.NewGuid();

        var analysis = new BoundedArchiveAnalysisRecord(1, 100, null, null);
        var result = new ArchiveInspectionResult(1, 100);

        var success = await ctx.LifecycleStore.TransitionArchiveInspectionAccepted(
            ctx.ClaimedJob.JobId,
            staleToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            result,
            analysis);

        success.Should().BeFalse();

        // Version should remain unchanged
        AssetVersion unchangedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        unchangedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.PENDING_INSPECTION);

        // Analysis must not exist
        var analysisExists = await ctx.Db.AssetArchiveAnalyses.AnyAsync(a => a.AssetVersionId == ctx.Version.Id);
        analysisExists.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionArchiveInspectionAccepted_WhenExpiredInDb_ShouldFailWithoutMutating()
    {
        // Setup job and immediately force its LeaseExpiresAt into the past
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);
        await ctx.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "LeaseExpiresAt" = clock_timestamp() - INTERVAL '10 seconds' WHERE "Id" = {0}""",
            ctx.ClaimedJob.JobId);

        var analysis = new BoundedArchiveAnalysisRecord(1, 100, null, null);
        var result = new ArchiveInspectionResult(1, 100);

        var success = await ctx.LifecycleStore.TransitionArchiveInspectionAccepted(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            result,
            analysis);

        success.Should().BeFalse();

        AssetVersion unchangedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        unchangedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.PENDING_INSPECTION);
    }

    [Fact]
    public async Task TransitionArchiveInspectionRejected_ShouldSetRejectedStatusAndErrorCode()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);

        var success = await ctx.LifecycleStore.TransitionArchiveInspectionRejected(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            errorCode: "ARCHIVE_CORRUPT",
            safeSummary: "The archive could not be decompressed safely.");

        success.Should().BeTrue();

        AssetVersion rejectedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        rejectedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.REJECTED);
        rejectedVersion.ProcessingErrorCode.Should().Be("ARCHIVE_CORRUPT");
        rejectedVersion.ProcessingErrorSummary.Should().Be("The archive could not be decompressed safely.");

        AssetProcessingJob job = await ctx.Db.AssetProcessingJobs.AsNoTracking().FirstAsync(j => j.Id == ctx.ClaimedJob.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.FAILED);
        job.ErrorCode.Should().Be("ARCHIVE_CORRUPT");
    }

    [Fact]
    public async Task TransitionMalwareScanClean_ShouldPromoteCandidateToCurrentMonotonically()
    {
        ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        // v1 is current READY version
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/test/v1.bin", versionNumber: 1, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        db.AssetVersions.Add(v1);

        // v2 is candidate version in PENDING_MALWARE_SCAN
        AssetVersion v2 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/test/v2.bin", versionNumber: 2, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        db.AssetVersions.Add(v2);
        await db.SaveChangesAsync();

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            asset.Id,
            v2.Id,
            AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1,
            initialDelay: TimeSpan.Zero,
            payload: new MalwareScanPayload("1.0"));

        IReadOnlyList<ClaimedAssetProcessingJob> claimed = await jobStore.ClaimPendingBatch(1, _leaseDuration, "test-worker-1");
        claimed.Should().HaveCount(1);
        ClaimedAssetProcessingJob claimedJob = claimed[0];

        AssetProcessingLifecycleStore lifecycleStore = CreateLifecycleStore(db);
        var cleanResult = new MalwareScanResult(IsClean: true);

        var success = await lifecycleStore.TransitionMalwareScanClean(
            claimedJob.JobId,
            claimedJob.LeaseToken,
            asset.Id,
            v2.Id,
            cleanResult);

        success.Should().BeTrue();

        // Candidate (v2) should be READY and IsCurrent = true
        AssetVersion candidate = await db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == v2.Id);
        candidate.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.READY);
        candidate.IsCurrent.Should().BeTrue();

        // Previous current (v1) should now be IsCurrent = false
        AssetVersion previous = await db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == v1.Id);
        previous.IsCurrent.Should().BeFalse();

        // MALWARE_SCAN job is SUCCEEDED
        AssetProcessingJob job = await db.AssetProcessingJobs.AsNoTracking().FirstAsync(j => j.Id == claimedJob.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
    }

    [Fact]
    public async Task TransitionMalwareScanRejected_ShouldSetRejectedStatus()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.MALWARE_SCAN);

        var success = await ctx.LifecycleStore.TransitionMalwareScanRejected(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            errorCode: "MALWARE_DETECTED",
            safeSummary: "Malware was detected in the archive.");

        success.Should().BeTrue();

        AssetVersion rejectedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        rejectedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.REJECTED);
        rejectedVersion.ProcessingErrorCode.Should().Be("MALWARE_DETECTED");
        rejectedVersion.IsCurrent.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionMalwareScanRejected_WhenPreviousReadyCurrentExists_ShouldNotDemotePrevious()
    {
        ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/test/v1.bin", versionNumber: 1, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        AssetVersion v2 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/test/v2.bin", versionNumber: 2, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        db.AssetVersions.AddRange(v1, v2);
        await db.SaveChangesAsync();

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(asset.Id, v2.Id, AssetProcessingJobType.MALWARE_SCAN, 1, TimeSpan.Zero, new MalwareScanPayload("1.0"));
        IReadOnlyList<ClaimedAssetProcessingJob> claimed = await jobStore.ClaimPendingBatch(1, _leaseDuration, "test-worker-1");
        claimed.Should().HaveCount(1);

        AssetProcessingLifecycleStore lifecycleStore = CreateLifecycleStore(db);
        var success = await lifecycleStore.TransitionMalwareScanRejected(
            claimed[0].JobId,
            claimed[0].LeaseToken,
            asset.Id,
            v2.Id,
            "MALWARE_DETECTED",
            ErrorCodesToErrorMessages.GetMessage(ErrorCodes.MALWARE_DETECTED));

        success.Should().BeTrue();
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == v1.Id)).IsCurrent.Should().BeTrue();
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == v1.Id)).ProcessingStatus.Should().Be(AssetVersionProcessingStatus.READY);
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == v2.Id)).IsCurrent.Should().BeFalse();
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == v2.Id)).ProcessingStatus.Should().Be(AssetVersionProcessingStatus.REJECTED);
    }

    [Fact]
    public async Task TransitionMalwareScanFailed_WhenPreviousReadyCurrentExists_ShouldNotDemotePrevious()
    {
        ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/test/v1.bin", versionNumber: 1, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        AssetVersion v2 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/test/v2.bin", versionNumber: 2, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        db.AssetVersions.AddRange(v1, v2);
        await db.SaveChangesAsync();

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(asset.Id, v2.Id, AssetProcessingJobType.MALWARE_SCAN, 1, TimeSpan.Zero, new MalwareScanPayload("1.0"));
        IReadOnlyList<ClaimedAssetProcessingJob> claimed = await jobStore.ClaimPendingBatch(1, _leaseDuration, "test-worker-1");
        claimed.Should().HaveCount(1);

        AssetProcessingLifecycleStore lifecycleStore = CreateLifecycleStore(db);
        var success = await lifecycleStore.TransitionMalwareScanFailed(
            claimed[0].JobId,
            claimed[0].LeaseToken,
            asset.Id,
            v2.Id,
            ErrorCodes.SCANNER_LIMIT_EXCEEDED,
            ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SCANNER_LIMIT_EXCEEDED));

        success.Should().BeTrue();
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == v1.Id)).IsCurrent.Should().BeTrue();
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == v1.Id)).ProcessingStatus.Should().Be(AssetVersionProcessingStatus.READY);
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == v2.Id)).IsCurrent.Should().BeFalse();
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == v2.Id)).ProcessingStatus.Should().Be(AssetVersionProcessingStatus.PROCESSING_FAILED);
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenResultIsNotClean_ShouldThrow()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.MALWARE_SCAN);
        var dirtyResult = new MalwareScanResult(IsClean: false);

        Func<Task<bool>> act = () => ctx.LifecycleStore.TransitionMalwareScanClean(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            dirtyResult);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*TransitionMalwareScanClean requires a clean scan result*");
    }

    [Fact]
    public async Task TransitionArchiveInspectionAccepted_WhenVersionNotInPendingInspection_ShouldReturnFalse()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);

        // Force version into a different state (already PENDING_MALWARE_SCAN)
        await ctx.Db.AssetVersions
            .Where(v => v.Id == ctx.Version.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ProcessingStatus, AssetVersionProcessingStatus.PENDING_MALWARE_SCAN));

        var analysis = new BoundedArchiveAnalysisRecord(FileCount: 1, TotalExpandedBytes: 100, ReadmeContent: null, ManifestMetadata: null);
        var result = new ArchiveInspectionResult(FileCount: 1, TotalSizeUncompressed: 100);

        var success = await ctx.LifecycleStore.TransitionArchiveInspectionAccepted(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            result,
            analysis);

        // Should return false: version is not in expected PENDING_INSPECTION state
        success.Should().BeFalse();

        // Version should remain in PENDING_MALWARE_SCAN (no state corruption)
        AssetVersion unchangedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        unchangedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenVersionNotInPendingMalwareScan_ShouldReturnFalse()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.MALWARE_SCAN);

        // Force version into READY (e.g. another worker already promoted it)
        await ctx.Db.AssetVersions
            .Where(v => v.Id == ctx.Version.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.ProcessingStatus, AssetVersionProcessingStatus.READY)
                .SetProperty(v => v.IsCurrent, true));

        var cleanResult = new MalwareScanResult(IsClean: true);

        var success = await ctx.LifecycleStore.TransitionMalwareScanClean(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            cleanResult);

        // Should return false: version is not in expected PENDING_MALWARE_SCAN state
        success.Should().BeFalse();

        // Version should remain READY and current (no state corruption)
        AssetVersion unchangedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        unchangedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.READY);
        unchangedVersion.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task TransitionArchiveInspectionRejected_WhenVersionNotInPendingInspection_ShouldReturnFalse()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);

        // Force version into READY (e.g. invalid state for archive rejection)
        await ctx.Db.AssetVersions
            .Where(v => v.Id == ctx.Version.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.ProcessingStatus, AssetVersionProcessingStatus.READY)
                .SetProperty(v => v.IsCurrent, true));

        var success = await ctx.LifecycleStore.TransitionArchiveInspectionRejected(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            "ARCHIVE_CORRUPT",
            "Archive is corrupt");

        success.Should().BeFalse();

        // Version should remain READY (not corrupted to REJECTED)
        AssetVersion unchangedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        unchangedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.READY);
    }

    [Fact]
    public async Task TransitionMalwareScanFailed_WhenVersionNotInPendingMalwareScan_ShouldReturnFalse()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.MALWARE_SCAN);

        // Force version into PENDING_INSPECTION (invalid state for malware scan failure)
        await ctx.Db.AssetVersions
            .Where(v => v.Id == ctx.Version.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.ProcessingStatus, AssetVersionProcessingStatus.PENDING_INSPECTION));

        var success = await ctx.LifecycleStore.TransitionMalwareScanFailed(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            "SCANNER_ERROR",
            "Malware scanner timed out");

        success.Should().BeFalse();

        AssetVersion unchangedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        unchangedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.PENDING_INSPECTION);
    }

    [Fact]
    public async Task TransitionArchiveInspectionRejected_WhenSummaryExceeds2000Chars_ShouldRuneBoundAndSucceed()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);
        var hugeSummary = new string('A', 3000);

        var success = await ctx.LifecycleStore.TransitionArchiveInspectionRejected(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            "ARCHIVE_INVALID",
            hugeSummary);

        success.Should().BeTrue();

        AssetVersion rejectedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        rejectedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.REJECTED);
        rejectedVersion.ProcessingErrorSummary!.Length.Should().Be(2000);
    }

    [Fact]
    public async Task TransitionProcessingFailed_WhenArchiveInspectionLeaseValid_ShouldFailJobAndVersionAtomically()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);

        var success = await ctx.LifecycleStore.TransitionProcessingFailed(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            "PROCESSING_TIMEOUT",
            "Asset processing timed out.");

        success.Should().BeTrue();

        AssetVersion failedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        failedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.PROCESSING_FAILED);
        failedVersion.ProcessingErrorCode.Should().Be("PROCESSING_TIMEOUT");

        AssetProcessingJob job = await ctx.Db.AssetProcessingJobs.AsNoTracking().FirstAsync(j => j.Id == ctx.ClaimedJob.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.FAILED);
    }

    [Fact]
    public async Task TransitionProcessingFailed_WhenMalwareScanLeaseValid_ShouldFailJobAndVersionAtomically()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.MALWARE_SCAN);

        var success = await ctx.LifecycleStore.TransitionProcessingFailed(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            AssetProcessingJobType.MALWARE_SCAN,
            "SCANNER_UNAVAILABLE",
            "The malware scanner is temporarily unavailable.");

        success.Should().BeTrue();

        AssetVersion failedVersion = await ctx.Db.AssetVersions.AsNoTracking().FirstAsync(v => v.Id == ctx.Version.Id);
        failedVersion.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.PROCESSING_FAILED);
        failedVersion.ProcessingErrorCode.Should().Be("SCANNER_UNAVAILABLE");

        AssetProcessingJob job = await ctx.Db.AssetProcessingJobs.AsNoTracking().FirstAsync(j => j.Id == ctx.ClaimedJob.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.FAILED);
    }

    [Fact]
    public async Task TransitionMalwareScanClean_ShouldCreateOneDurableNotificationAndOutbox()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.MALWARE_SCAN);
        var success = await ctx.LifecycleStore.TransitionMalwareScanClean(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            new MalwareScanResult(IsClean: true));

        success.Should().BeTrue();
        await AssertSingleTerminalNotification(
            ctx.Db,
            ctx.Author.Id,
            ctx.Asset.Id,
            ctx.Version.Id,
            NotificationKind.ASSET_PROCESSING_READY,
            "READY");
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenReplayed_ShouldNotDuplicateNotification()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.MALWARE_SCAN);
        var first = await ctx.LifecycleStore.TransitionMalwareScanClean(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            new MalwareScanResult(IsClean: true));
        var second = await ctx.LifecycleStore.TransitionMalwareScanClean(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            new MalwareScanResult(IsClean: true));

        first.Should().BeTrue();
        second.Should().BeTrue();
        (await ctx.Db.UserNotifications.AsNoTracking().CountAsync()).Should().Be(1);
        (await ctx.Db.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.Type == OutboxMessageTypes.NOTIFICATION_DISPATCH)).Should().Be(1);
    }

    [Fact]
    public async Task TransitionArchiveInspectionRejected_ShouldCreateRejectedNotification()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);
        var success = await ctx.LifecycleStore.TransitionArchiveInspectionRejected(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            "ARCHIVE_CORRUPT",
            "The archive could not be decompressed safely.");

        success.Should().BeTrue();
        await AssertSingleTerminalNotification(
            ctx.Db,
            ctx.Author.Id,
            ctx.Asset.Id,
            ctx.Version.Id,
            NotificationKind.ASSET_PROCESSING_REJECTED,
            "REJECTED");
    }

    [Fact]
    public async Task TransitionProcessingFailed_ShouldCreateFailedNotification()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.MALWARE_SCAN);
        var success = await ctx.LifecycleStore.TransitionProcessingFailed(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            AssetProcessingJobType.MALWARE_SCAN,
            "SCANNER_UNAVAILABLE",
            "The malware scanner is temporarily unavailable.");

        success.Should().BeTrue();
        await AssertSingleTerminalNotification(
            ctx.Db,
            ctx.Author.Id,
            ctx.Asset.Id,
            ctx.Version.Id,
            NotificationKind.ASSET_PROCESSING_FAILED,
            "PROCESSING_FAILED");
    }

    [Fact]
    public async Task TransitionArchiveInspectionAccepted_ShouldNotCreateNotification()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);
        var success = await ctx.LifecycleStore.TransitionArchiveInspectionAccepted(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            new ArchiveInspectionResult(1, 100),
            new BoundedArchiveAnalysisRecord(1, 100, null, null));

        success.Should().BeTrue();
        (await ctx.Db.UserNotifications.AsNoTracking().CountAsync()).Should().Be(0);
        (await ctx.Db.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.Type == OutboxMessageTypes.NOTIFICATION_DISPATCH)).Should().Be(0);
    }

    [Fact]
    public async Task TransitionArchiveInspectionRejected_WhenLeaseLost_ShouldNotCreateNotification()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);
        var success = await ctx.LifecycleStore.TransitionArchiveInspectionRejected(
            ctx.ClaimedJob.JobId,
            Guid.NewGuid(),
            ctx.Asset.Id,
            ctx.Version.Id,
            "ARCHIVE_CORRUPT",
            "The archive could not be decompressed safely.");

        success.Should().BeFalse();
        (await ctx.Db.UserNotifications.AsNoTracking().CountAsync()).Should().Be(0);
        (await ctx.Db.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.Type == OutboxMessageTypes.NOTIFICATION_DISPATCH)).Should().Be(0);
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenVersionNotPending_ShouldRollbackWithoutNotification()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.MALWARE_SCAN);
        await ctx.Db.AssetVersions
            .Where(v => v.Id == ctx.Version.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.ProcessingStatus, AssetVersionProcessingStatus.READY)
                .SetProperty(v => v.IsCurrent, true));

        var success = await ctx.LifecycleStore.TransitionMalwareScanClean(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            new MalwareScanResult(IsClean: true));

        success.Should().BeFalse();
        (await ctx.Db.UserNotifications.AsNoTracking().CountAsync()).Should().Be(0);
        (await ctx.Db.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.Type == OutboxMessageTypes.NOTIFICATION_DISPATCH)).Should().Be(0);
        AssetProcessingJob job = await ctx.Db.AssetProcessingJobs.AsNoTracking().FirstAsync(j => j.Id == ctx.ClaimedJob.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.RUNNING);
    }

    [Fact]
    public async Task TransitionArchiveInspectionRejected_WhenReplayed_ShouldNotDuplicateNotification()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);
        var first = await ctx.LifecycleStore.TransitionArchiveInspectionRejected(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            "ARCHIVE_CORRUPT",
            "The archive could not be decompressed safely.");
        var second = await ctx.LifecycleStore.TransitionArchiveInspectionRejected(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            "ARCHIVE_CORRUPT",
            "The archive could not be decompressed safely.");

        first.Should().BeTrue();
        second.Should().BeTrue();
        (await ctx.Db.UserNotifications.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(AssetProcessingJobType.ARCHIVE_INSPECTION)]
    [InlineData(AssetProcessingJobType.MALWARE_SCAN)]
    public async Task RecoverExpiredExhaustedSecurityJobs_WhenFinalAttemptExpires_ShouldFailVersionAndNotify(
        AssetProcessingJobType jobType)
    {
        TestContext ctx = await SetupRunningJob(jobType);
        await ctx.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "MaxAttempts" = {1}, "AttemptCount" = {2} WHERE "Id" = {0}""",
            ctx.ClaimedJob.JobId, 1, 1);
        await ctx.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "LeaseExpiresAt" = CURRENT_TIMESTAMP - INTERVAL '1 second' WHERE "Id" = {0}""",
            ctx.ClaimedJob.JobId);

        (await ctx.JobStore.RecoverExpiredLeases()).Should().Be(0);
        (await ctx.LifecycleStore.RecoverExpiredExhaustedSecurityJobs()).Should().Be(1);

        ctx.Db.ChangeTracker.Clear();
        AssetVersion version = await ctx.Db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == ctx.Version.Id);
        version.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.PROCESSING_FAILED);
        version.ProcessingErrorCode.Should().Be(ErrorCodes.LEASE_EXPIRED);

        AssetProcessingJob job = await ctx.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == ctx.ClaimedJob.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.FAILED);
        job.Stage.Should().Be("FAILED_LEASE_EXPIRED");
        job.ErrorCode.Should().Be(ErrorCodes.LEASE_EXPIRED);

        await AssertSingleTerminalNotification(
            ctx.Db,
            ctx.Author.Id,
            ctx.Asset.Id,
            ctx.Version.Id,
            NotificationKind.ASSET_PROCESSING_FAILED,
            "PROCESSING_FAILED");
    }

    [Fact]
    public async Task RecoverExpiredExhaustedSecurityJobs_WhenReplayed_ShouldNotDuplicateNotification()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.ARCHIVE_INSPECTION);
        await ctx.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "MaxAttempts" = {1}, "AttemptCount" = {2} WHERE "Id" = {0}""",
            ctx.ClaimedJob.JobId, 1, 1);
        await ctx.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "LeaseExpiresAt" = CURRENT_TIMESTAMP - INTERVAL '1 second' WHERE "Id" = {0}""",
            ctx.ClaimedJob.JobId);

        (await ctx.LifecycleStore.RecoverExpiredExhaustedSecurityJobs()).Should().Be(1);
        (await ctx.LifecycleStore.RecoverExpiredExhaustedSecurityJobs()).Should().Be(0);
        (await ctx.Db.UserNotifications.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TransitionProcessingFailed_WhenTerminalFailure_CreatesOutboxMessageWithPendingStatus()
    {
        TestContext ctx = await SetupRunningJob(AssetProcessingJobType.MALWARE_SCAN);

        var transitioned = await ctx.LifecycleStore.TransitionMalwareScanFailed(
            ctx.ClaimedJob.JobId,
            ctx.ClaimedJob.LeaseToken,
            ctx.Asset.Id,
            ctx.Version.Id,
            ErrorCodes.MALWARE_DETECTED,
            "Malware detected during security scan");

        transitioned.Should().BeTrue();

        UserNotification notification = await ctx.Db.UserNotifications.AsNoTracking().SingleAsync();
        notification.SourceOutboxMessageId.Should().NotBeNull();

        OutboxMessage outbox = await ctx.Db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == notification.SourceOutboxMessageId!.Value);
        outbox.Status.Should().Be(OutboxMessageStatus.PENDING);
        outbox.AttemptCount.Should().Be(0);
        outbox.Type.Should().Be(OutboxMessageTypes.NOTIFICATION_DISPATCH);
    }

    private static async Task AssertSingleTerminalNotification(
        ApplicationDbContext db,
        Guid recipientUserId,
        Guid assetId,
        Guid assetVersionId,
        NotificationKind kind,
        string processingStatus)
    {
        List<UserNotification> notifications = await db.UserNotifications.AsNoTracking().ToListAsync();
        notifications.Should().ContainSingle();
        UserNotification notification = notifications[0];
        notification.RecipientUserId.Should().Be(recipientUserId);
        notification.Kind.Should().Be(kind);
        notification.SourceOutboxMessageId.Should().NotBeNull();
        notification.MetadataJson.Should().Contain(assetId.ToString());
        notification.MetadataJson.Should().Contain(assetVersionId.ToString());
        notification.MetadataJson.Should().Contain(processingStatus);
        notification.MetadataJson.Should().NotContain("storageKey");
        notification.MetadataJson.Should().NotContain("package.zip");

        OutboxMessage outbox = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Type == OutboxMessageTypes.NOTIFICATION_DISPATCH);
        outbox.Id.Should().Be(notification.SourceOutboxMessageId!.Value);
        outbox.Status.Should().Be(OutboxMessageStatus.PENDING);
        outbox.AttemptCount.Should().Be(0);
        outbox.ReplayCount.Should().Be(0);
    }
}
