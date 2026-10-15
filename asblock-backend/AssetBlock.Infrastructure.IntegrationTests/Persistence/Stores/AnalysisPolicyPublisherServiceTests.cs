using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Proves the approved policy manifest is produced even when the database is not reachable at
/// startup: publication retries with bounded backoff until it actually persists, is idempotent when
/// it succeeds, and stops without an endless loop when the deployment declares a manifest that can
/// never be published. Readiness re-evaluation is what turns the persisted row into open claims, so
/// no restart is needed for recovery.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class AnalysisPolicyPublisherServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PublishLoop_WhenFirstAttemptFails_ShouldRetryAndPersistOnceTheDatabaseRecovers()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AnalysisPolicyManifest manifest = ValidManifest();
        var flaky = new FailingPublisher(RealPublisher(db), failures: 1);

        AnalysisPolicyPublisherService service = CreateService(flaky, Declared(manifest));
        await service.StartAsync(CancellationToken.None);
        await WaitForRow(fixture, manifest.PolicyDigest);
        await service.StopAsync(CancellationToken.None);

        // The manifest appears without a restart: the bounded retry, not a restart, is the recovery.
        flaky.Calls.Should().BeGreaterThan(1);
        CodeAnalysisPolicyVersion stored = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();
        stored.PolicyDigest.Should().Be(manifest.PolicyDigest);
        stored.ModelKey.Should().Be(AdmittedReportTestEvidence.MODEL_KEY);
    }

    [Fact]
    public async Task PublishLoop_WhenManifestIsRejected_ShouldStopWithoutRetryingForeverOrWriting()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var counting = new FailingPublisher(RealPublisher(db), failures: 0);

        // An empty tool identity is not a publishable approved manifest, so publication must give up
        // instead of retrying an identity that can never succeed.
        AnalysisPolicyManifestOptions complete = Declared(ValidManifest());
        AnalysisPolicyPublisherService service = CreateService(
            counting,
            new AnalysisPolicyManifestOptions
            {
                PolicyKey = complete.PolicyKey,
                PolicyDigest = complete.PolicyDigest,
                SchemaName = complete.SchemaName,
                StageMatrixJson = complete.StageMatrixJson,
                VerifierMode = complete.VerifierMode,
                VerifierReason = complete.VerifierReason,
                ToolIdentitiesJson = "{}",
                LimitsJson = complete.LimitsJson,
                SecurityValidityJson = complete.SecurityValidityJson,
                ModelKey = complete.ModelKey,
                IndexKey = complete.IndexKey,
                ReferenceSnapshotId = complete.ReferenceSnapshotId
            });

        await service.StartAsync(CancellationToken.None);
        await WaitForCalls(counting, expected: 1);
        await service.StopAsync(CancellationToken.None);

        counting.Calls.Should().Be(1);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PublishLoop_WhenAnalysisIsDisabled_ShouldPublishNothing()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var counting = new FailingPublisher(RealPublisher(db), failures: 0);

        AnalysisPolicyPublisherService service = new(
            CreateProvider(counting).GetRequiredService<IServiceScopeFactory>(),
            MsOptions.Create(new CodeAnalysisOptions { Enabled = false }),
            new ImmediateTimeProvider(),
            NullLogger<AnalysisPolicyPublisherService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        counting.Calls.Should().Be(0);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// Waits for the retry loop to publish the approved row. The poll uses its own context because
    /// the publishing loop runs on another thread, and a DbContext is never thread-safe. The retry
    /// delays are immediate under the test time provider, so this only guards scheduling jitter.
    /// </summary>
    private static async Task WaitForRow(PostgresFixture fixture, string policyDigest)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            await using ApplicationDbContext poll = fixture.CreateDbContext();
            if (await poll.CodeAnalysisPolicyVersions.AsNoTracking().AnyAsync(p => p.PolicyDigest == policyDigest))
            {
                return;
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitForCalls(FailingPublisher publisher, int expected)
    {
        for (var attempt = 0; attempt < 500 && publisher.Calls < expected; attempt++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task PublishLoop_WhenFailureIsPermanent_ShouldStopImmediatelyWithoutRetrying()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        // A permanent provider fault, exactly like an unavailable or misconfigured database.
        var publisher = new FailingPublisher(RealPublisher(db), failures: int.MaxValue, permanent: true);

        AnalysisPolicyPublisherService service = CreateService(publisher, Declared(ValidManifest()));
        await service.StartAsync(CancellationToken.None);
        await WaitForCalls(publisher, expected: 1);
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        // One attempt only: a permanent fault is surfaced instead of being retried as an outage.
        publisher.Calls.Should().Be(1);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PublishLoop_WhenTransientFailureNeverClears_ShouldExhaustItsBoundedAttempts()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var publisher = new FailingPublisher(RealPublisher(db), failures: int.MaxValue, permanent: false);

        AnalysisPolicyPublisherService service = CreateService(publisher, Declared(ValidManifest()));
        await service.StartAsync(CancellationToken.None);
        await WaitForCalls(publisher, expected: 12);
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        // The bounded budget is spent, then publication stops instead of retrying forever.
        publisher.Calls.Should().Be(12);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PublishLoop_WhenStoppedDuringRetry_ShouldStopWithoutFurtherAttempts()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var publisher = new FailingPublisher(RealPublisher(db), failures: int.MaxValue, permanent: false);

        AnalysisPolicyPublisherService service = CreateService(publisher, Declared(ValidManifest()));
        await service.StartAsync(CancellationToken.None);
        await WaitForCalls(publisher, expected: 1);

        await service.StopAsync(CancellationToken.None);
        var callsAtStop = publisher.Calls;
        await Task.Delay(100);

        // Cancellation is honoured: no further attempt is made once the host is stopping.
        publisher.Calls.Should().Be(callsAtStop);
    }

    private static IAnalysisPolicyPublisher RealPublisher(ApplicationDbContext db) =>
        new AnalysisPolicyPublisher(db, NullLogger<AnalysisPolicyPublisher>.Instance);

    private static AnalysisPolicyManifest ValidManifest() =>
        new("code-analysis-policy-v1",
            AdmittedReportTestEvidence.NewHex64(),
            "code-analysis-v1",
            AdmittedReportTestEvidence.StageMatrixJson(),
            "PENDING_RESEARCH_EVALUATION",
            "Retrieval quality research evaluation has not been reviewed.",
            AdmittedReportTestEvidence.ToolIdentitiesJson(),
            AdmittedReportTestEvidence.LimitsJson(),
            AdmittedReportTestEvidence.SecurityValidityJson(),
            AdmittedReportTestEvidence.MODEL_KEY,
            AdmittedReportTestEvidence.INDEX_KEY,
            AdmittedReportTestEvidence.REFERENCE_SNAPSHOT_ID);

    private static AnalysisPolicyManifestOptions Declared(AnalysisPolicyManifest manifest) =>
        new()
        {
            PolicyKey = manifest.PolicyKey,
            PolicyDigest = manifest.PolicyDigest,
            SchemaName = manifest.SchemaName,
            StageMatrixJson = manifest.StageMatrixJson,
            VerifierMode = manifest.VerifierMode,
            VerifierReason = manifest.VerifierReason,
            ToolIdentitiesJson = manifest.ToolIdentitiesJson,
            LimitsJson = manifest.LimitsJson,
            SecurityValidityJson = manifest.SecurityValidityJson,
            ModelKey = manifest.ModelKey,
            IndexKey = manifest.IndexKey,
            ReferenceSnapshotId = manifest.ReferenceSnapshotId
        };

    private static AnalysisPolicyPublisherService CreateService(
        IAnalysisPolicyPublisher publisher,
        AnalysisPolicyManifestOptions declared) =>
        new(CreateProvider(publisher).GetRequiredService<IServiceScopeFactory>(),
            MsOptions.Create(new CodeAnalysisOptions { Enabled = true, PolicyManifest = declared }),
            new ImmediateTimeProvider(),
            NullLogger<AnalysisPolicyPublisherService>.Instance);

    private static ServiceProvider CreateProvider(IAnalysisPolicyPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => publisher);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A publisher that fails its first attempts the way an unavailable database would, then
    /// delegates to the real publisher so the approved row is persisted exactly as production
    /// persists it. A permanent failure models a provider, permission or mapping fault that can
    /// never succeed on a later attempt.
    /// </summary>
    private sealed class FailingPublisher(
        IAnalysisPolicyPublisher inner,
        int failures,
        bool permanent = false) : IAnalysisPolicyPublisher
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<AnalysisPolicyPublishResult> Publish(
            AnalysisPolicyManifest manifest,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) <= failures)
            {
                throw permanent
                    ? new InvalidOperationException("Simulated permanent provider fault.")
                    : new TimeoutException("Simulated transient database outage.");
            }

            return inner.Publish(manifest, cancellationToken);
        }
    }

    /// <summary>Retry delays elapse immediately, so the bounded backoff loop runs without waiting.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new ImmediateTimer();
        }

        private sealed class ImmediateTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
