using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

/// <summary>
/// Concurrent HTTP races on one case. A start gate releases both requests together. A side connection holds the asset row
/// lock that every review and submit path takes first, until both requests are queued behind it. The overlap is proven
/// twice: the two requests start within a small window, and PostgreSQL reports both waiting on the asset lock at once.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class ReviewRaceIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    // Each test gets its own database on the shared container. The races leave many open cases behind, which would crowd
    // the moderator queue pages that other controller tests read from the shared database.
    private readonly string _databaseName = "race_" + Guid.NewGuid().ToString("N");

    // The seller analysis summary compares the pinned policy digest with the configured one. Only this class pins the product
    // digest, so the shared host and the other controller tests keep their configuration.
    private WebApplicationFactory<Program> _factory = null!;

    private const int ITERATIONS = 20;
    private static readonly TimeSpan _maxStartGap = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _lockQueueTimeout = TimeSpan.FromSeconds(20);
    private const string ASSET_LOCK_QUERY_PATTERN = "%FROM assets WHERE%FOR UPDATE%";

    private sealed record CaseSeed(Guid AssetId, Guid VersionId, Guid WorkspaceId, Guid SubmissionId, Guid ReportId);

    private sealed record SubmitSeed(Guid AssetId, Guid VersionId, Guid WorkspaceId, long WorkspaceRevision, long CaseRevision);

    private sealed record CaseEffects(
        ModerationSubmissionState State,
        Guid? ClaimHolderId,
        long CaseRevision,
        int Decisions,
        int Snapshots,
        Guid? PointerSnapshotId,
        int HistoryRows,
        int DecisionOutbox,
        int CaseAudit);

    private sealed record RaceResult(HttpResponseMessage First, HttpResponseMessage Second, int LockWaiters, TimeSpan StartGap) : IDisposable
    {
        public void Dispose()
        {
            First.Dispose();
            Second.Dispose();
        }
    }

    [Fact]
    public async Task Race_TwoModeratorsDecideAndClaim_ShouldOnlyDecide()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(_factory);
        (HttpClient holder, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(_factory, AppRoles.MODERATOR);
        (HttpClient other, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(_factory, AppRoles.MODERATOR);
        using (owner)
        using (holder)
        using (other)
        {
            for (var iteration = 0; iteration < ITERATIONS; iteration++)
            {
                CaseSeed seed = await SeedClaimedCaseAsync(ownerName, holder);
                var basis = await ReadBasisAsync(seed);
                var decision = ModerationDecisionFixture.DecisionBody(Guid.NewGuid(), basis, "APPROVE");
                var claimRevision = (await ReadEffectsAsync(seed)).CaseRevision;

                using RaceResult race = await RaceAsync(seed.AssetId,
                    () => ModerationDecisionFixture.PostDecisionAsync(holder, seed.SubmissionId, decision),
                    () => ModerationDecisionFixture.PostClaimAsync(other, seed.SubmissionId, Guid.NewGuid(), claimRevision));

                AssertOverlap(race, iteration);
                AssertExactlyOneSucceeded(race, iteration, "decision or claim");
                race.First.StatusCode.Should().Be(HttpStatusCode.OK, $"iteration {iteration}: the holder's decision wins; a second moderator cannot take a held case");
                var loserBody = await LoserBodyAsync(race);
                (loserBody.Contains(ErrorCodes.ERR_MODERATION_CASE_HELD) || loserBody.Contains(ErrorCodes.ERR_MODERATION_CASE_NOT_ACTIVE))
                    .Should().BeTrue($"iteration {iteration}: the second claim gets the standard conflict, got {loserBody}");

                CaseEffects effects = await ReadEffectsAsync(seed);
                effects.State.Should().Be(ModerationSubmissionState.APPROVED, $"iteration {iteration}");
                effects.Decisions.Should().Be(1, $"iteration {iteration}: exactly one terminal decision row");
                effects.Snapshots.Should().Be(1, $"iteration {iteration}: one publication snapshot");
                effects.PointerSnapshotId.Should().NotBeNull($"iteration {iteration}: the pointer moved once");
                effects.DecisionOutbox.Should().Be(1, $"iteration {iteration}: one decision outbox row");
            }
        }
    }

    [Fact]
    public async Task Race_SameModeratorSameOperationTwice_ShouldReplayOneEffectSet()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(_factory);
        (HttpClient holder, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(_factory, AppRoles.MODERATOR);
        using (owner)
        using (holder)
        {
            for (var iteration = 0; iteration < ITERATIONS; iteration++)
            {
                CaseSeed seed = await SeedClaimedCaseAsync(ownerName, holder);
                var decision = ModerationDecisionFixture.DecisionBody(Guid.NewGuid(), await ReadBasisAsync(seed), "APPROVE");

                using RaceResult race = await RaceAsync(seed.AssetId,
                    () => ModerationDecisionFixture.PostDecisionAsync(holder, seed.SubmissionId, decision),
                    () => ModerationDecisionFixture.PostDecisionAsync(holder, seed.SubmissionId, decision));

                AssertOverlap(race, iteration);
                race.First.StatusCode.Should().Be(HttpStatusCode.OK, $"iteration {iteration}");
                race.Second.StatusCode.Should().Be(HttpStatusCode.OK, $"iteration {iteration}: the identical retry replays");
                JsonElement first = await ReadJsonAsync(race.First);
                JsonElement second = await ReadJsonAsync(race.Second);
                foreach (var field in new[] { "decisionId", "state", "publicationSnapshotId", "promoted", "caseRevision" })
                {
                    second.GetProperty(field).GetRawText().Should().Be(first.GetProperty(field).GetRawText(), $"iteration {iteration}: {field} is identical");
                }

                new[] { first.GetProperty("replayed").GetBoolean(), second.GetProperty("replayed").GetBoolean() }
                    .Should().BeEquivalentTo([false, true], $"iteration {iteration}: one fresh effect and one replay");

                CaseEffects effects = await ReadEffectsAsync(seed);
                effects.Decisions.Should().Be(1, $"iteration {iteration}: one decision row");
                effects.Snapshots.Should().Be(1, $"iteration {iteration}: one snapshot");
                effects.DecisionOutbox.Should().Be(1, $"iteration {iteration}: one decision outbox row");
            }
        }
    }

    [Fact]
    public async Task Race_SameModeratorDifferentOperations_ShouldAcceptOneDecision()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(_factory);
        (HttpClient holder, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(_factory, AppRoles.MODERATOR);
        using (owner)
        using (holder)
        {
            for (var iteration = 0; iteration < ITERATIONS; iteration++)
            {
                CaseSeed seed = await SeedClaimedCaseAsync(ownerName, holder);
                var basis = await ReadBasisAsync(seed);
                var approve = ModerationDecisionFixture.DecisionBody(Guid.NewGuid(), basis, "APPROVE");
                var reject = ModerationDecisionFixture.DecisionBody(Guid.NewGuid(), basis, "REJECT");

                using RaceResult race = await RaceAsync(seed.AssetId,
                    () => ModerationDecisionFixture.PostDecisionAsync(holder, seed.SubmissionId, approve),
                    () => ModerationDecisionFixture.PostDecisionAsync(holder, seed.SubmissionId, reject));

                AssertOverlap(race, iteration);
                AssertExactlyOneSucceeded(race, iteration, "decision");
                var approveWon = race.First.StatusCode == HttpStatusCode.OK;
                var loserBody = await LoserBodyAsync(race);
                loserBody.Should().Contain(ErrorCodes.ERR_MODERATION_DECISION_EXISTS, $"iteration {iteration}: the second decision is refused as already decided");

                CaseEffects effects = await ReadEffectsAsync(seed);
                effects.Decisions.Should().Be(1, $"iteration {iteration}: one decision row");
                effects.State.Should().Be(approveWon ? ModerationSubmissionState.APPROVED : ModerationSubmissionState.REJECTED, $"iteration {iteration}: the winner's state");
                effects.Snapshots.Should().Be(approveWon ? 1 : 0, $"iteration {iteration}: only an approval publishes");
                effects.DecisionOutbox.Should().Be(1, $"iteration {iteration}: one decision outbox row");
            }
        }
    }

    [Fact]
    public async Task Race_TwoSubmitsBySameOwner_ShouldCreateOneActiveCase()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(_factory);
        using (owner)
        {
            for (var iteration = 0; iteration < ITERATIONS; iteration++)
            {
                SubmitSeed sameSeed = await SeedSubmitReadyVersionAsync(ownerName);
                var sameOperation = Guid.NewGuid();

                using (RaceResult replay = await RaceAsync(sameSeed.AssetId,
                    () => PostSubmitAsync(owner, sameSeed, sameOperation),
                    () => PostSubmitAsync(owner, sameSeed, sameOperation)))
                {
                    AssertOverlap(replay, iteration);
                    var firstBody = await replay.First.Content.ReadAsStringAsync();
                    replay.First.StatusCode.Should().Be(HttpStatusCode.OK, $"iteration {iteration}: {firstBody}");
                    replay.Second.StatusCode.Should().Be(HttpStatusCode.OK, $"iteration {iteration}: the same operation replays");
                    JsonElement a = await ReadJsonAsync(replay.First);
                    JsonElement b = await ReadJsonAsync(replay.Second);
                    b.GetProperty("submissionId").GetRawText().Should().Be(a.GetProperty("submissionId").GetRawText(), $"iteration {iteration}");
                }

                await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    (await db.ModerationSubmissions.CountAsync(s => s.AssetVersionId == sameSeed.VersionId))
                        .Should().Be(1, $"iteration {iteration}: one case for the version after the replay");
                }

                SubmitSeed distinctSeed = await SeedSubmitReadyVersionAsync(ownerName);
                using RaceResult distinct = await RaceAsync(distinctSeed.AssetId,
                    () => PostSubmitAsync(owner, distinctSeed, Guid.NewGuid()),
                    () => PostSubmitAsync(owner, distinctSeed, Guid.NewGuid()));
                AssertOverlap(distinct, iteration);
                AssertExactlyOneSucceeded(distinct, iteration, "submit");
                var loserBody = await LoserBodyAsync(distinct);
                (loserBody.Contains(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE) || loserBody.Contains(ErrorCodes.ERR_MODERATION_SUBMISSION_BLOCKED))
                    .Should().BeTrue($"iteration {iteration}: the second submit gets the standard conflict, got {loserBody}");

                await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
                ApplicationDbContext verifyDb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                (await verifyDb.ModerationSubmissions.CountAsync(s => s.AssetVersionId == distinctSeed.VersionId
                    && (s.State == ModerationSubmissionState.SUBMITTED || s.State == ModerationSubmissionState.IN_REVIEW)))
                    .Should().Be(1, $"iteration {iteration}: exactly one active submission for the version");
            }
        }
    }

    [Fact]
    public async Task Race_DecisionAndOwnerEvidenceEdit_ShouldEndInOneOfTwoConsistentStates()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(_factory);
        (HttpClient holder, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(_factory, AppRoles.MODERATOR);
        using (owner)
        using (holder)
        {
            for (var iteration = 0; iteration < ITERATIONS; iteration++)
            {
                CaseSeed seed = await SeedClaimedCaseAsync(ownerName, holder);
                var decision = ModerationDecisionFixture.DecisionBody(Guid.NewGuid(), await ReadBasisAsync(seed), "APPROVE");
                var workspaceRevision = await ReadWorkspaceRevisionAsync(seed.WorkspaceId);
                var evidence = new
                {
                    workspaceId = seed.WorkspaceId,
                    reportId = seed.ReportId,
                    expectedWorkspaceRevision = workspaceRevision,
                    expectedEvidenceRevision = 0,
                    operationId = Guid.NewGuid(),
                    text = "Owner evidence note raced against the decision."
                };

                using RaceResult race = await RaceAsync(seed.AssetId,
                    () => ModerationDecisionFixture.PostDecisionAsync(holder, seed.SubmissionId, decision),
                    () => owner.PutAsJsonAsync(EvidencePath(seed), evidence));

                AssertOverlap(race, iteration);
                CaseEffects effects = await ReadEffectsAsync(seed);
                if (race.First.StatusCode == HttpStatusCode.OK)
                {
                    race.Second.StatusCode.Should().Be(HttpStatusCode.OK, $"iteration {iteration}: the evidence edit applies after the terminal decision");
                    effects.State.Should().Be(ModerationSubmissionState.APPROVED, $"iteration {iteration}: decision first, case stays terminal");
                    effects.Snapshots.Should().Be(1, $"iteration {iteration}: one snapshot for the approved case");
                }
                else
                {
                    race.First.StatusCode.Should().Be(HttpStatusCode.Conflict, $"iteration {iteration}: the decision is refused, not failed");
                    (await race.First.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_CASE_NOT_ACTIVE, $"iteration {iteration}");
                    race.Second.StatusCode.Should().Be(HttpStatusCode.OK, $"iteration {iteration}: the evidence edit wins and supersedes the active case");
                    effects.State.Should().Be(ModerationSubmissionState.SUPERSEDED, $"iteration {iteration}: the active case is superseded");
                    effects.Snapshots.Should().Be(0, $"iteration {iteration}: a superseded case never publishes");
                    effects.Decisions.Should().Be(0, $"iteration {iteration}: no decision row for the refused decision");
                }
            }
        }
    }

    [Fact]
    public async Task Race_ReviewerReleaseAndOwnerWithdraw_ShouldApplyExactlyOneTransition()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(_factory);
        (HttpClient holder, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(_factory, AppRoles.MODERATOR);
        using (owner)
        using (holder)
        {
            for (var iteration = 0; iteration < ITERATIONS; iteration++)
            {
                CaseSeed seed = await SeedClaimedCaseAsync(ownerName, holder);
                CaseEffects before = await ReadEffectsAsync(seed);
                var releaseOperation = Guid.NewGuid();
                var withdraw = new { expectedCaseRevision = before.CaseRevision, operationId = Guid.NewGuid() };

                using RaceResult race = await RaceAsync(seed.AssetId,
                    () => PostReleaseAsync(holder, seed.SubmissionId, releaseOperation, before.CaseRevision),
                    () => owner.PostAsJsonAsync(WithdrawPath(seed.SubmissionId), withdraw));

                AssertOverlap(race, iteration);
                AssertExactlyOneSucceeded(race, iteration, "release or withdraw");
                CaseEffects after = await ReadEffectsAsync(seed);
                if (race.First.StatusCode == HttpStatusCode.OK)
                {
                    (await race.Second.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_SUBMISSION_WITHDRAW_CONFLICT, $"iteration {iteration}");
                    after.State.Should().Be(ModerationSubmissionState.SUBMITTED, $"iteration {iteration}: released, not withdrawn");
                    after.ClaimHolderId.Should().BeNull($"iteration {iteration}: the release cleared the claim");
                }
                else
                {
                    (await race.First.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_CASE_NOT_ACTIVE, $"iteration {iteration}: release after withdraw");
                    after.State.Should().Be(ModerationSubmissionState.WITHDRAWN, $"iteration {iteration}: withdraw wins");
                }

                after.HistoryRows.Should().Be(before.HistoryRows + 1, $"iteration {iteration}: one history row for the single winning transition");
                after.Decisions.Should().Be(0, $"iteration {iteration}: no decision row");
                after.Snapshots.Should().Be(0, $"iteration {iteration}: no snapshot");
            }
        }
    }

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync(_databaseName);
        _factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("CodeAnalysis:PolicyDigest", ReviewAnalysisPolicy.DIGEST);
        });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await fixture.DropIsolatedDatabaseAsync(_databaseName);
    }

    // ---- race mechanics ----

    private async Task<RaceResult> RaceAsync(Guid lockedAssetId, Func<Task<HttpResponseMessage>> first, Func<Task<HttpResponseMessage>> second)
    {
        // The holder uses its own scope so the transaction runs on a connection with the host's full credentials.
        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext holder = holderScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using IDbContextTransaction tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM assets WHERE "Id" = {lockedAssetId} FOR UPDATE""").ToListAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedAt = new long[2];
        Task<HttpResponseMessage> a = StartAtGateAsync(first, gate.Task, startedAt, 0);
        Task<HttpResponseMessage> b = StartAtGateAsync(second, gate.Task, startedAt, 1);
        gate.SetResult();

        int waiters;
        try
        {
            waiters = await WaitForLockWaitersAsync(2);
        }
        finally
        {
            await tx.CommitAsync();
        }

        HttpResponseMessage[] responses = await Task.WhenAll(a, b);
        TimeSpan gap = Stopwatch.GetElapsedTime(Math.Min(startedAt[0], startedAt[1]), Math.Max(startedAt[0], startedAt[1]));
        return new RaceResult(responses[0], responses[1], waiters, gap);
    }

    private static async Task<HttpResponseMessage> StartAtGateAsync(Func<Task<HttpResponseMessage>> send, Task gate, long[] startedAt, int slot)
    {
        await gate;
        startedAt[slot] = Stopwatch.GetTimestamp();
        return await send();
    }

    private async Task<int> WaitForLockWaitersAsync(int expected)
    {
        using var deadline = new CancellationTokenSource(_lockQueueTimeout);
        await using AsyncServiceScope observerScope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext observer = observerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var best = 0;
        while (!deadline.IsCancellationRequested)
        {
            var count = await observer.Database.SqlQueryRaw<long>(
                "SELECT count(*) AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE {0}",
                ASSET_LOCK_QUERY_PATTERN).SingleAsync(deadline.Token);
            var waiting = (int)count;
            best = Math.Max(best, waiting);
            if (waiting >= expected)
            {
                return waiting;
            }

            try
            {
                await Task.Delay(20, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return best;
    }

    private static void AssertOverlap(RaceResult race, int iteration)
    {
        race.LockWaiters.Should().BeGreaterThanOrEqualTo(2, $"iteration {iteration}: both requests must queue behind the asset lock together");
        race.StartGap.Should().BeLessThan(_maxStartGap, $"iteration {iteration}: both requests start after the same gate");
    }

    private static void AssertExactlyOneSucceeded(RaceResult race, int iteration, string label)
    {
        var firstWon = race.First.StatusCode == HttpStatusCode.OK;
        var secondWon = race.Second.StatusCode == HttpStatusCode.OK;
        (firstWon ^ secondWon).Should().BeTrue(
            $"iteration {iteration}: exactly one {label} succeeds, got {(int)race.First.StatusCode} and {(int)race.Second.StatusCode}");
    }

    private static async Task<string> LoserBodyAsync(RaceResult race)
    {
        HttpResponseMessage loser = race.First.StatusCode == HttpStatusCode.OK ? race.Second : race.First;
        return await loser.Content.ReadAsStringAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> PostReleaseAsync(HttpClient reviewer, Guid submissionId, Guid operationId, long expectedCaseRevision)
    {
        var path = "/" + ApiRoutes.Moderation.RELEASE.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
        return reviewer.PostAsJsonAsync(path, new { operationId, expectedCaseRevision });
    }

    private static string WithdrawPath(Guid submissionId) =>
        "/" + ApiRoutes.Moderation.SUBMISSION_WITHDRAW.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);

    private static string EvidencePath(CaseSeed seed) =>
        "/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_EVIDENCE
            .Replace("{id:guid}", seed.AssetId.ToString(), StringComparison.Ordinal)
            .Replace("{versionId:guid}", seed.VersionId.ToString(), StringComparison.Ordinal);

    // ---- seeding ----

    private async Task<CaseSeed> SeedClaimedCaseAsync(string ownerName, HttpClient holder)
    {
        CaseSeed seed = await SeedReviewableCaseAsync(ownerName);
        using HttpResponseMessage claimed = await ModerationDecisionFixture.PostClaimAsync(holder, seed.SubmissionId, Guid.NewGuid(), 1);
        claimed.StatusCode.Should().Be(HttpStatusCode.OK, await claimed.Content.ReadAsStringAsync());
        return seed;
    }

    private async Task<CaseSeed> SeedReviewableCaseAsync(string ownerName)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User owner = await db.Users.SingleAsync(u => u.Username == ownerName);
        Asset asset = AddAsset(db, owner, out AssetVersion version);
        ModerationSubmission submission = ModerationDecisionFixture.AddReviewableSubmission(db, asset, version, owner);
        await db.SaveChangesAsync();
        Guid reportId = await ModerationDecisionFixture.PrepareDecisionReadySubmission(db, submission);
        return new CaseSeed(asset.Id, version.Id, submission.WorkspaceId, submission.Id, reportId);
    }

    private async Task<SubmitSeed> SeedSubmitReadyVersionAsync(string ownerName)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User owner = await db.Users.SingleAsync(u => u.Username == ownerName);
        Asset asset = AddAsset(db, owner, out AssetVersion version);
        var workspace = new AssetDraftWorkspace
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceVersionScopeKey = version.Id,
            ScopeId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AssetDraftWorkspaces.Add(workspace);
        await db.SaveChangesAsync();
        await ModerationDecisionFixture.PrepareAnalysisReadyVersion(
            db, version, workspace, asset, owner.Id, "Race submit title", null, CodeAnalysisCoverageLevel.FULL, 0);
        await db.SaveChangesAsync();
        AssetDraftWorkspace saved = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == workspace.Id);
        return new SubmitSeed(asset.Id, version.Id, workspace.Id, saved.WorkspaceRevision, saved.CaseRevision);
    }

    private static Asset AddAsset(ApplicationDbContext db, User owner, out AssetVersion version)
    {
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var category = new Category { Id = Guid.NewGuid(), Name = "Race " + assetId.ToString("N"), Slug = "race-" + assetId.ToString("N"), CreatedAt = now };
        AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        db.Categories.Add(category);
        var asset = new Asset { Id = assetId, AuthorId = owner.Id, CategoryId = category.Id, Title = "Race subject", Description = "", Price = 0, CreatedAt = now };
        db.Assets.Add(asset);
        version = new AssetVersion
        {
            Id = versionId,
            AssetId = assetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = $"assets/{assetId:N}/source.bin",
            FileName = "source.zip",
            ContentLength = 1,
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(versionId.ToString()))),
            ReleaseNotes = "Race release",
            LicenseCode = license.Code,
            LicenseTemplateVersion = license.TemplateVersion,
            LicenseDisplayName = license.DisplayName,
            LicenseTerms = license.TermsPlainText,
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = now,
            CreatedAt = now
        };
        db.AssetVersions.Add(version);
        return asset;
    }

    private static Task<HttpResponseMessage> PostSubmitAsync(HttpClient owner, SubmitSeed seed, Guid operationId)
    {
        var path = "/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_SUBMISSIONS
            .Replace("{id:guid}", seed.AssetId.ToString(), StringComparison.Ordinal)
            .Replace("{versionId:guid}", seed.VersionId.ToString(), StringComparison.Ordinal);
        return owner.PostAsJsonAsync(path, new
        {
            workspaceId = seed.WorkspaceId,
            expectedWorkspaceRevision = seed.WorkspaceRevision,
            expectedCaseRevision = seed.CaseRevision,
            operationId
        });
    }

    private async Task<object> ReadBasisAsync(CaseSeed seed) =>
        await ModerationDecisionFixture.ReadDecisionBasis(_factory.Services.GetRequiredService<IServiceScopeFactory>(), seed.SubmissionId, seed.ReportId);

    private async Task<long> ReadWorkspaceRevisionAsync(Guid workspaceId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.AssetDraftWorkspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => w.WorkspaceRevision).SingleAsync();
    }

    private async Task<CaseEffects> ReadEffectsAsync(CaseSeed seed)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == seed.SubmissionId);
        Asset asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == seed.AssetId);
        var caseKeys = new[] { seed.AssetId.ToString(), seed.VersionId.ToString(), seed.SubmissionId.ToString() };
        var assetKey = seed.AssetId.ToString();
        var submissionKey = seed.SubmissionId.ToString();
        return new CaseEffects(
            row.State,
            row.ClaimHolderId,
            row.CaseRevision,
            await db.ModerationDecisionRecords.CountAsync(d => d.SubmissionId == seed.SubmissionId),
            await db.PublicationSnapshots.CountAsync(p => p.AssetId == seed.AssetId),
            asset.CurrentPublicationSnapshotId,
            await db.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == seed.SubmissionId),
            await db.OutboxMessages.CountAsync(o => o.Type == OutboxMessageTypes.MODERATION_DECISION
                && (o.Payload.Contains(assetKey) || o.Payload.Contains(submissionKey))),
            await db.AuditLogs.CountAsync(a => a.ResourceId != null && caseKeys.Contains(a.ResourceId)));
    }
}
