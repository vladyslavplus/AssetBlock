using System.Text.Json;
using System.Text.Json.Nodes;
using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.SaveSellerEvidence;
using AssetBlock.Application.UseCases.Assets.SubmitAssetVersion;
using AssetBlock.Application.UseCases.Assets.WithdrawSubmission;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;
namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

public sealed partial class CodeAnalysisReportFinalizerPostgresTests
{
    [Theory]
    [InlineData(AppRoles.USER)]
    [InlineData(AppRoles.MODERATOR)]
    [InlineData(AppRoles.ADMIN)]
    public async Task SellerResponses_WhenMaterialFindingsAreAnswered_ShouldPermitExplicitLinkedEvidenceResubmissionAndUnchangedWithdrawal(string ownerRole)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 2);
        await db.Users.Where(u => u.Id == seed.Source.Asset.AuthorId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, ownerRole));
        ModerationFoundationStore store = ResponseStore(db);
        var save = new SaveSellerEvidenceCommandHandler(store);
        var submit = new SubmitAssetVersionCommandHandler(store, ResponseAnalysisStore(db));
        CodeAnalysisReportFinding[] before = await db.CodeAnalysisReportFindings.AsNoTracking().OrderBy(f => f.Id).ToArrayAsync();
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        current.OutstandingCount.Should().Be(2);
        SellerAnalysisFindingsPageDto ownerFindings = (await ResponseAnalysisStore(db).GetOwnerFindings(seed.Source.Version.Id, seed.ReportId, seed.Source.Asset.AuthorId, 1))!;
        ownerFindings.Items.Should().OnlyContain(f => f.ResponseRequired && !f.HasResponse);
        SellerAnalysisSummaryDto ownerSummary = (await ResponseAnalysisStore(db).GetOwnerSummary(seed.Source.Version.Id, seed.Source.Asset.AuthorId))!;
        foreach (var json in new[] { JsonSerializer.Serialize(ownerFindings, new JsonSerializerOptions(JsonSerializerDefaults.Web)), JsonSerializer.Serialize(ownerSummary, new JsonSerializerOptions(JsonSerializerDefaults.Web)) })
        {
            foreach (var field in new[] { "foreignSourceId", "referenceItemId", "tokenRank", "tokenScore", "semanticRank", "semanticScore", "channels", "queryFileId", "material", "retrieval" })
            {
                json.Should().NotContain("\"" + field + "\":");
            }
            json.Should().NotContain(new string('b', 64)).And.NotContain(new string('c', 64));
        }
        Result<Guid> blocked = await submit.Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        blocked.Status.Should().Be(ResultStatus.Conflict);
        blocked.Errors.Should().Contain(ErrorCodes.ERR_FINDING_RESPONSE_REQUIRED);
        SaveSellerEvidenceCommand first = ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.INDEPENDENT_IMPLEMENTATION, "  private response sentinel  ");
        Result<SellerEvidenceSavedDto> firstSaved = await save.Handle(first, CancellationToken.None);
        firstSaved.IsSuccess.Should().BeTrue();
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        current.OutstandingCount.Should().Be(1);
        Result<SellerEvidenceSavedDto> secondSaved = await save.Handle(ResponseCommand(seed, current, seed.Findings[1], SellerResponseType.NO_EVIDENCE, ""), CancellationToken.None);
        secondSaved.IsSuccess.Should().BeTrue();
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        current.OutstandingCount.Should().Be(0);
        current.Items.Should().HaveCount(2).And.OnlyContain(r => r.IsCurrent);
        (await db.SellerResponses.AsNoTracking().SingleAsync(r => r.Id == firstSaved.Value.ResponseId)).SellerEvidenceRevision.Should().Be(1);
        Result<SellerEvidenceSavedDto> replay = await save.Handle(first, CancellationToken.None);
        replay.Value.Should().Be(firstSaved.Value with { Replayed = true });
        (await save.Handle(first with { Text = "Changed payload" }, CancellationToken.None)).Errors.Should().Contain(ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT);
        (await save.Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.NO_EVIDENCE, "") with { ExpectedEvidenceRevision = 0 }, CancellationToken.None))
            .Errors.Should().Contain(ErrorCodes.ERR_SELLER_EVIDENCE_STALE);
        Result<Guid> submitted = await submit.Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        submitted.IsSuccess.Should().BeTrue();
        var runsBefore = await db.CodeAnalysisRuns.CountAsync();
        ModerationSubmission trackedCase = await db.ModerationSubmissions.SingleAsync(s => s.Id == submitted.Value);
        var priorCaseRevision = trackedCase.CaseRevision;
        await db.ModerationSubmissions.Where(s => s.Id == submitted.Value).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, ModerationSubmissionState.IN_REVIEW).SetProperty(c => c.CaseRevision, priorCaseRevision + 1));
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Result<SellerEvidenceSavedDto> edited = await save.Handle(new SaveSellerEvidenceCommand(seed.Source.Asset.AuthorId,
            seed.Source.Asset.Id, seed.Source.Version.Id, current.WorkspaceId, seed.ReportId, null, null,
            "Additional private permission evidence", current.WorkspaceRevision, current.EvidenceRevision, Guid.NewGuid()), CancellationToken.None);
        edited.IsSuccess.Should().BeTrue();
        ModerationSubmission superseded = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submitted.Value);
        superseded.CaseRevision.Should().Be(priorCaseRevision + 2);
        superseded.State.Should().Be(ModerationSubmissionState.SUPERSEDED);
        (await db.ModerationSubmissions.CountAsync()).Should().Be(1);
        (await db.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == submitted.Value)).Should().Be(2);
        (await db.CodeAnalysisRuns.CountAsync()).Should().Be(runsBefore);
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        current.OutstandingCount.Should().Be(0);
        Result<Guid> resubmitted = await submit.Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        resubmitted.IsSuccess.Should().BeTrue();
        ModerationSubmission linked = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == resubmitted.Value);
        linked.PreviousSubmissionId.Should().Be(submitted.Value);
        linked.CodeAnalysisReportHeaderId.Should().Be(seed.ReportId);
        linked.SellerEvidenceRevision.Should().Be(3);
        var withdraw = new WithdrawSubmissionCommandHandler(store);
        (await withdraw.Handle(new WithdrawSubmissionCommand(seed.Source.Asset.AuthorId, linked.Id, linked.CaseRevision, Guid.NewGuid()), CancellationToken.None)).IsSuccess.Should().BeTrue();
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Result<Guid> unchanged = await submit.Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        unchanged.IsSuccess.Should().BeTrue();
        ModerationSubmission afterWithdrawal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == unchanged.Value);
        afterWithdrawal.PreviousSubmissionId.Should().Be(linked.Id);
        afterWithdrawal.SellerEvidenceDigest.Should().Be(linked.SellerEvidenceDigest);
        afterWithdrawal.CodeAnalysisReportHeaderId.Should().Be(seed.ReportId);
        (await db.SellerResponses.CountAsync()).Should().Be(2);
        (await db.CodeAnalysisReportFindings.AsNoTracking().OrderBy(f => f.Id).ToArrayAsync()).Should().BeEquivalentTo(before);
        (await db.ModerationFindingDispositions.AnyAsync()).Should().BeFalse();
        (await db.Assets.Where(a => a.Id == seed.Source.Asset.Id).Select(a => a.CurrentPublicationSnapshotId).SingleAsync()).Should().BeNull();
        (await db.AuditLogs.Select(a => a.MetadataJson).ToListAsync()).Should().OnlyContain(text => text == null || !text.Contains("private response sentinel") && !text.Contains("Additional private permission evidence"));
        (await db.OutboxMessages.Select(o => o.Payload).ToListAsync()).Should().OnlyContain(text => !text.Contains("private response sentinel"));
        User newOwner = TestData.CreateUser("response-next-owner", "response-next-owner@example.test");
        db.Users.Add(newOwner);
        await db.SaveChangesAsync();
        await db.Assets.Where(a => a.Id == seed.Source.Asset.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.AuthorId, newOwner.Id));
        (await save.Handle(first, CancellationToken.None)).Status.Should().Be(ResultStatus.Forbidden);
    }

    [Theory]
    [InlineData("foreign-owner")]
    [InlineData("different-report")]
    [InlineData("different-version")]
    [InlineData("different-finding")]
    [InlineData("non-material")]
    [InlineData("oversize")]
    [InlineData("empty-explanation")]
    [InlineData("failed-stage")]
    [InlineData("missing-stage")]
    [InlineData("stale-digest")]
    [InlineData("nonowner-admin")]
    public async Task SellerResponses_WhenBindingOrInputIsInvalid_ShouldNotAppend(string condition)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        ModerationFoundationStore store = ResponseStore(db);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        SaveSellerEvidenceCommand command = ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.PRIOR_WORK, "Explanation");
        switch (condition)
        {
            case "foreign-owner":
                command = command with { OwnerId = Guid.NewGuid() };
                break;
            case "different-report":
                command = command with { ReportId = Guid.NewGuid() };
                break;
            case "different-version":
                command = command with { AssetVersionId = Guid.NewGuid() };
                break;
            case "different-finding":
                command = command with { FindingId = Guid.NewGuid() };
                break;
            case "non-material":
                await db.CodeAnalysisReportFindings.Where(f => f.Id == seed.Findings[0]).ExecuteUpdateAsync(s => s.SetProperty(f => f.RetrievalEvidenceJson,
                    """{"queryFileId":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","queryUnit":"FILE","channels":"SEMANTIC","tokenRank":null,"tokenScore":null,"semanticRank":1,"semanticScore":0.4,"material":false}"""));
                break;
            case "oversize":
                command = command with { Text = new string('x', 2001) };
                break;
            case "empty-explanation":
                command = command with { Text = " " };
                break;
            case "failed-stage":
                await db.CodeAnalysisReportStages.Where(s => s.ReportId == seed.ReportId && s.Stage == "TOKEN").ExecuteUpdateAsync(s => s.SetProperty(v => v.Outcome, CodeAnalysisStageOutcome.FAILED));
                break;
            case "missing-stage":
                await db.CodeAnalysisReportStages.Where(s => s.ReportId == seed.ReportId && s.Stage == "TOKEN").ExecuteDeleteAsync();
                break;
            case "stale-digest":
                await db.CodeAnalysisRuns.Where(r => r.Id == seed.Source.RunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeclarationDigest, new string('f', 64)));
                break;
            case "nonowner-admin":
                User admin = TestData.CreateUser("foreign-response-admin", "foreign-response-admin@example.test");
                admin.Role = AppRoles.ADMIN;
                db.Users.Add(admin);
                await db.SaveChangesAsync();
                command = command with { OwnerId = admin.Id };
                break;
        }
        Result<SellerEvidenceSavedDto> result = await new SaveSellerEvidenceCommandHandler(store).Handle(command, CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
        (await db.SellerResponses.CountAsync()).Should().Be(0);
        (await db.AssetSellerEvidenceRevisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SellerResponses_WhenSubmitOperationsRace_ShouldKeepOneActiveCase()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        ModerationFoundationStore store = ResponseStore(db);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        (await new SaveSellerEvidenceCommandHandler(store).Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.NO_EVIDENCE, ""), CancellationToken.None)).IsSuccess.Should().BeTrue();
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        await using ApplicationDbContext first = fixture.CreateDbContext();
        await using ApplicationDbContext second = fixture.CreateDbContext();
        Result<Guid>[] results = await Task.WhenAll(
            new SubmitAssetVersionCommandHandler(ResponseStore(first), ResponseAnalysisStore(first)).Handle(SubmitResponseVersion(seed, current), CancellationToken.None),
            new SubmitAssetVersionCommandHandler(ResponseStore(second), ResponseAnalysisStore(second)).Handle(SubmitResponseVersion(seed, current), CancellationToken.None));
        results.Count(r => r.IsSuccess).Should().Be(1);
        (await db.ModerationSubmissions.CountAsync(s => s.State == ModerationSubmissionState.SUBMITTED)).Should().Be(1);
    }


    [Fact]
    public async Task FindingDispositions_WhenCallerTransactionRollsBack_ShouldRemainAppendOnlyAndEnforceAttemptUniqueness()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        ModerationFoundationStore store = ResponseStore(db);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        await new SaveSellerEvidenceCommandHandler(store).Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.NO_EVIDENCE, ""), CancellationToken.None);
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Result<Guid> submitted = await new SubmitAssetVersionCommandHandler(store, ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        ModerationSubmission submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync();
        User moderator = TestData.CreateUser("disposition-reviewer", "disposition@example.test");
        moderator.Role = AppRoles.MODERATOR;
        db.Users.Add(moderator);
        await db.SaveChangesAsync();
        FindingDispositionInput[] input = [new(seed.Findings[0], FindingDispositionType.EXPLAINED, "  Bounded private rationale.  ")];
        Func<Task> withoutTransaction = () => store.AppendFindingDispositions(submitted.Value, moderator.Id, submission.CaseRevision, input);
        await withoutTransaction.Should().ThrowAsync<InvalidOperationException>();
        await using (Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync())
        {
            (await store.AppendFindingDispositions(submitted.Value, seed.Source.Asset.AuthorId, submission.CaseRevision, input)).Should().BeFalse();
            (await store.AppendFindingDispositions(submitted.Value, moderator.Id, submission.CaseRevision + 1, input)).Should().BeFalse();
            (await store.AppendFindingDispositions(submitted.Value, moderator.Id, submission.CaseRevision, [new(Guid.NewGuid(), FindingDispositionType.EXPLAINED, "Unknown finding")])).Should().BeFalse();
            (await store.AppendFindingDispositions(submitted.Value, moderator.Id, submission.CaseRevision, input)).Should().BeTrue();
            ModerationFindingDisposition row = await db.ModerationFindingDispositions.SingleAsync();
            row.Rationale.Should().Be("Bounded private rationale.");
            db.Entry(row).Property(d => d.Rationale).CurrentValue = "Cannot edit";
            Func<Task> edit = () => db.SaveChangesAsync();
            await edit.Should().ThrowAsync<InvalidOperationException>();
            db.ChangeTracker.Clear();
            Func<Task> duplicate = () => store.AppendFindingDispositions(submitted.Value, moderator.Id, submission.CaseRevision, input);
            await duplicate.Should().ThrowAsync<DbUpdateException>();
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        (await db.ModerationFindingDispositions.CountAsync()).Should().Be(0);
        SellerResponse response = await db.SellerResponses.SingleAsync();
        db.SellerResponses.Remove(response);
        Func<Task> delete = () => db.SaveChangesAsync();
        await delete.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("type")]
    [InlineData("rationale")]
    [InlineData("revision")]
    [InlineData("finding")]
    public async Task FindingDispositions_WhenDatabaseConstraintIsViolated_ShouldReject(string violation)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        ModerationFoundationStore store = ResponseStore(db);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        await new SaveSellerEvidenceCommandHandler(store).Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.NO_EVIDENCE, ""), CancellationToken.None);
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Result<Guid> submitted = await new SubmitAssetVersionCommandHandler(store, ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        db.ModerationFindingDispositions.Add(new ModerationFindingDisposition
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Source.Asset.Id,
            AssetVersionId = seed.Source.Version.Id,
            SubmissionId = submitted.Value,
            ReportId = seed.ReportId,
            FindingId = violation == "finding" ? Guid.NewGuid() : seed.Findings[0],
            ModeratorUserId = seed.Source.Asset.AuthorId,
            CaseRevision = violation == "revision" ? 0 : 2,
            Disposition = violation == "type" ? (FindingDispositionType)999 : FindingDispositionType.EXPLAINED,
            Rationale = violation == "rationale" ? " " : "Reason",
            CreatedAt = DateTimeOffset.UtcNow
        });
        Func<Task> write = () => db.SaveChangesAsync();
        await write.Should().ThrowAsync<DbUpdateException>();
    }


    [Fact]
    public async Task SellerResponses_WhenAnotherAnswerIsSaved_ShouldRetainImmutableHistoryAndSelectNewestAnswer()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        ModerationFoundationStore store = ResponseStore(db);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        var save = new SaveSellerEvidenceCommandHandler(store);
        Result<SellerEvidenceSavedDto> first = await save.Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.PRIOR_WORK, "First explanation"), CancellationToken.None);
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Result<SellerEvidenceSavedDto> second = await save.Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.NO_EVIDENCE, ""), CancellationToken.None);
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        current.Items.Should().HaveCount(2);
        current.Items.Single(r => r.IsCurrent).Id.Should().Be(second.Value.ResponseId!.Value);
        current.Items.Single(r => r.Id == first.Value.ResponseId).Should().Match<SellerResponseDto>(r => !r.IsCurrent && r.Text == "First explanation" && r.EvidenceRevision == 1);
        current.OutstandingCount.Should().Be(0);
        SellerAnalysisFindingsPageDto page = (await ResponseAnalysisStore(db).GetOwnerFindings(seed.Source.Version.Id, seed.ReportId, seed.Source.Asset.AuthorId, 1))!;
        page.Items.Single().HasResponse.Should().BeTrue();
        page.Items.Single().ResponseRequired.Should().BeTrue();
    }

    [Theory]
    [InlineData("type")]
    [InlineData("text")]
    [InlineData("finding")]
    [InlineData("duplicate")]
    public async Task SellerResponses_WhenDatabaseConstraintIsViolated_ShouldReject(string violation)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        ModerationFoundationStore store = ResponseStore(db);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Result<SellerEvidenceSavedDto> saved = await new SaveSellerEvidenceCommandHandler(store).Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.NO_EVIDENCE, ""), CancellationToken.None);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
        Func<Task> write = violation switch
        {
            "type" => () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE seller_responses SET \"ResponseType\" = 'INVALID' WHERE \"Id\" = {saved.Value.ResponseId}"),
            "text" => () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE seller_responses SET \"Text\" = {new string('x', 2001)} WHERE \"Id\" = {saved.Value.ResponseId}"),
            "finding" => () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE seller_responses SET \"FindingId\" = {Guid.NewGuid()} WHERE \"Id\" = {saved.Value.ResponseId}"),
            _ => () => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO seller_responses SELECT {Guid.NewGuid()}, \"AssetId\", \"AssetVersionId\", \"WorkspaceId\", \"WorkspaceVersionScopeKey\", \"ReportId\", \"FindingId\", \"SellerEvidenceRevision\", \"ResponseType\", \"Text\", \"ActorUserId\", \"CreatedAt\", \"UpdatedAt\" FROM seller_responses WHERE \"Id\" = {saved.Value.ResponseId}")
        };
        await write.Should().ThrowAsync<Npgsql.PostgresException>();
        await transaction.RollbackAsync();
    }


    [Fact]
    public async Task SellerResponses_WhenOwnerSummaryIsRead_ShouldLoadEvidenceRequirementsOnce()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        var counter = new FindingDetailReadCounter();
        await using ApplicationDbContext readDb = fixture.CreateDbContext(options => options.AddInterceptors(counter));
        SellerAnalysisSummaryDto summary = (await ResponseAnalysisStore(readDb).GetOwnerSummary(seed.Source.Version.Id, seed.Source.Asset.AuthorId))!;
        summary.OutstandingResponseCount.Should().Be(1);
        counter.Reads.Should().Be(1);
    }
    private sealed class FindingDetailReadCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM code_analysis_report_findings", StringComparison.Ordinal)
                && !command.CommandText.Contains("count(", StringComparison.OrdinalIgnoreCase))
            { Reads++; }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record ResponseReviewSeed(SeedContext Source, Guid WorkspaceId, Guid ReportId, Guid[] Findings);
    private static ModerationFoundationStore ResponseStore(ApplicationDbContext db) => new(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
    private static CodeAnalysisSellerStore ResponseAnalysisStore(ApplicationDbContext db) => new(db,
        Substitute.For<IAnalysisRunEnqueuer>(), Substitute.For<ISecurityProvenanceStore>(), Substitute.For<IAuditWriter>(), TimeProvider.System,
        MsOptions.Create(new CodeAnalysisOptions { Enabled = true, PolicyKey = ReviewAnalysisPolicy.KEY, PolicyDigest = ReviewAnalysisPolicy.DIGEST, ServiceKey = "code-analysis" }));
    private static SubmitAssetVersionCommand SubmitResponseVersion(ResponseReviewSeed seed, SellerResponsesPageDto current) => new(seed.Source.Asset.AuthorId,
        seed.Source.Asset.Id, seed.Source.Version.Id, current.WorkspaceId, current.WorkspaceRevision, current.CaseRevision, Guid.NewGuid());
    private static SaveSellerEvidenceCommand ResponseCommand(ResponseReviewSeed seed, SellerResponsesPageDto current, Guid findingId, SellerResponseType type, string text) => new(seed.Source.Asset.AuthorId,
        seed.Source.Asset.Id, seed.Source.Version.Id, current.WorkspaceId, seed.ReportId, findingId, type, text, current.WorkspaceRevision, current.EvidenceRevision, Guid.NewGuid());

    private static async Task<ResponseReviewSeed> SeedCurrentMaterialReview(ApplicationDbContext db, int candidates)
    {
        SeedContext seed = await SeedQueued(db);
        var workspace = new AssetDraftWorkspace
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            WorkspaceVersionScopeKey = seed.Version.Id,
            ScopeId = Guid.NewGuid(),
            MaterialMetadataHeadRevision = 1,
            SourceDeclarationHeadRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var declarationId = Guid.NewGuid();
        var materialId = Guid.NewGuid();
        const string declaration = """{"redistributionAcknowledged":true,"ownContributionSummary":"Original implementation.","components":[]}""";
        const string material = """{"title":"Review subject"}""";
        var declarationDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(declaration)));
        var materialDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
        db.AssetDraftWorkspaces.Add(workspace);
        db.AssetSourceDeclarationRevisions.Add(new AssetSourceDeclarationRevision
        {
            Id = declarationId,
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = seed.Version.Id,
            Revision = 1,
            AuthorUserId = seed.Asset.AuthorId,
            ContentDigest = declarationDigest,
            SchemaVersion = 1,
            PayloadJson = declaration,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.AssetMaterialMetadataRevisions.Add(new AssetMaterialMetadataRevision
        {
            Id = materialId,
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = seed.Version.Id,
            Revision = 1,
            AuthorUserId = seed.Asset.AuthorId,
            ContentDigest = materialDigest,
            SchemaVersion = 1,
            PayloadJson = material,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await db.CodeAnalysisRuns.Where(r => r.Id == seed.RunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.AssetDraftWorkspaceId, workspace.Id)
            .SetProperty(r => r.DeclarationRevisionId, declarationId).SetProperty(r => r.MaterialRevisionId, materialId)
            .SetProperty(r => r.DeclarationDigest, declarationDigest).SetProperty(r => r.MaterialDigest, materialDigest));
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "response-worker")).Single();
        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        JsonObject body = JsonNode.Parse(BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.PolicyDigest))!.AsObject();
        body["findings"]![0]!["foreign"]!["fileId"] = new string('b', 64);
        if (candidates == 2)
        {
            JsonNode second = body["findings"]![0]!.DeepClone();
            second["findingId"] = "second-material-finding";
            second["foreign"]!["fileId"] = new string('c', 64);
            second["retrieval"]!["tokenRank"] = 2;
            second["retrieval"]!["semanticRank"] = 2;
            body["findings"]!.AsArray().Add(second);
            body["retrievalSummary"] = PythonAnalysisEnvelope.RetrievalSummary(2);
            body["comparedScope"]!["comparedFiles"] = 2;
            body["stages"]!.AsArray().Single(s => s!["stage"]!.GetValue<string>() == "UNION")!["counts"]!["unionFindings"] = 2;
        }
        var finalizer = new CodeAnalysisReportFinalizer(db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        (await finalizer.FinalizeConfirmed(job.JobId, job.LeaseToken, PythonAnalysisEnvelope.RegenerateDerivedIdentity(body).ToJsonString())).Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        Guid reportId = await db.CodeAnalysisReportHeaders.Select(h => h.Id).SingleAsync();
        Guid[] findings = await db.CodeAnalysisReportFindings.OrderBy(f => f.FindingKey).Select(f => f.Id).ToArrayAsync();
        return new(seed, workspace.Id, reportId, findings);
    }
}
