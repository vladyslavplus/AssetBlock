using Ardalis.Result;
using AssetBlock.Application.UseCases.Moderation.AssessCoverage;
using AssetBlock.Application.UseCases.Moderation.CoverageReview;
using AssetBlock.Application.UseCases.Moderation.ListCoverageRequests;
using AssetBlock.Application.UseCases.Moderation.ReadReviewEvidence;
using AssetBlock.Application.UseCases.Moderation.RequestCoverageReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;
namespace AssetBlock.Application.Tests.UseCases.Moderation;

public sealed class CoverageReviewTests
{
    [Theory]
    [InlineData(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE)]
    [InlineData(ErrorCodes.ERR_DECLARATION_INCOMPLETE)]
    [InlineData(ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE)]
    [InlineData(ErrorCodes.ERR_ANALYSIS_REPORT_NOT_TRUSTED)]
    public void Map_WhenReportGateFails_ShouldPreserveConflictReason(string reason)
    {
        Result<CoverageReviewSaved> result = CoverageReviewResults.Map(new ReviewAccessResult<CoverageReviewSaved>(ReviewAccessStatus.CONFLICT, ReasonCode: reason));
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(reason);
        CoverageReviewResults.Map(new ReviewAccessResult<CoverageReviewSaved>(ReviewAccessStatus.SECURITY_RESTRICTED)).Status.Should().Be(ResultStatus.Forbidden);
    }
    [Fact]
    public void Map_WhenAuditUnavailable_ShouldDistinguishEvidenceReads()
    {
        var result = new ReviewAccessResult<CoverageReviewSaved>(ReviewAccessStatus.AUDIT_UNAVAILABLE);
        CoverageReviewResults.Map(result).Errors.Should().Contain(ErrorCodes.ERR_REVIEW_AUDIT_UNAVAILABLE);
        CoverageReviewResults.Map(result, evidenceRead: true).Errors.Should().Contain(ErrorCodes.ERR_EVIDENCE_AUDIT_UNAVAILABLE);
    }
    private static CoverageAssessmentRequest Input() => new(Guid.NewGuid(), 1, new string('a', 64), CoverageAssessmentOutcome.ACCEPTED,
        ["Markdown"], [new("README.md", "Markdown", "Documentation", true, RestrictedEvidenceKind.ARCHIVE_README)], true, "Read stored scope", "review/completed", "Reviewed.");
    [Theory]
    [InlineData("operation")]
    [InlineData("digest")]
    [InlineData("outcome")]
    [InlineData("plan")]
    [InlineData("rationale")]
    [InlineData("competence")]
    [InlineData("scope")]
    [InlineData("scope-null")]
    [InlineData("manageable")]
    [InlineData("completion")]
    public void Validate_WhenAssessmentIsInvalid_ShouldReject(string field)
    {
        CoverageAssessmentRequest input = Input();
        input = field switch
        {
            "operation" => input with { OperationId = Guid.Empty },
            "digest" => input with { ExpectedTupleDigest = "bad" },
            "outcome" => input with { Outcome = (CoverageAssessmentOutcome)999 },
            "plan" => input with { ReviewPlan = " " },
            "rationale" => input with { Rationale = new string('x', 2001) },
            "competence" => input with { Competence = [] },
            "scope" => input with { Scope = [] },
            "scope-null" => input with { Scope = [null!] },
            "manageable" => input with { ManageableScope = false },
            _ => input with { CompletedPlanReference = "" }
        };
        new AssessCoverageCommandValidator().Validate(new AssessCoverageCommand(Guid.NewGuid(), Guid.NewGuid(), input)).IsValid.Should().BeFalse();
    }
    [Fact]
    public void Validate_WhenAssessmentIsBoundedAndComplete_ShouldAccept()
    {
        new AssessCoverageCommandValidator().Validate(new AssessCoverageCommand(Guid.NewGuid(), Guid.NewGuid(), Input())).IsValid.Should().BeTrue();
        new ReadReviewEvidenceQueryValidator().Validate(new ReadReviewEvidenceQuery(Guid.NewGuid(), Guid.NewGuid(), true, (RestrictedEvidenceKind)999, null)).IsValid.Should().BeFalse();
        new ListCoverageRequestsQueryValidator().Validate(new ListCoverageRequestsQuery(Guid.NewGuid(), 0)).IsValid.Should().BeFalse();
    }
    [Theory]
    [InlineData(AppRoles.ADMIN)]
    [InlineData(AppRoles.USER)]
    [InlineData(null)]
    public async Task Handle_WhenCallerIsNotLiveModerator_ShouldDenyBeforeStore(string? role)
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        IUserStore users = Substitute.For<IUserStore>();
        var actor = Guid.NewGuid();
        users.GetPersistedRole(actor, Arg.Any<CancellationToken>()).Returns(role is null ? null : new UserPersistedRole(actor, role, 1));
        (await new AssessCoverageCommandHandler(store, users).Handle(new(actor, Guid.NewGuid(), Input()), CancellationToken.None)).Status.Should().Be(ResultStatus.Forbidden);
        (await new ReadReviewEvidenceQueryHandler(store, users).Handle(new(actor, Guid.NewGuid(), true, RestrictedEvidenceKind.ARCHIVE_README, null), CancellationToken.None)).Status.Should().Be(ResultStatus.Forbidden);
        (await new ListCoverageRequestsQueryHandler(store, users).Handle(new(actor, 1), CancellationToken.None)).Status.Should().Be(ResultStatus.Forbidden);
        store.ReceivedCalls().Should().BeEmpty();
    }
    [Theory]
    [InlineData(ReviewAccessStatus.STALE, ResultStatus.Conflict)]
    [InlineData(ReviewAccessStatus.CONFLICT, ResultStatus.Conflict)]
    [InlineData(ReviewAccessStatus.AUDIT_UNAVAILABLE, ResultStatus.Unavailable)]
    public async Task Handle_WhenRequestStoreFails_ShouldPreserveSafeError(ReviewAccessStatus status, ResultStatus expected)
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        store.RequestCoverageReview(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CoverageReviewCreateRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ReviewAccessResult<CoverageReviewSaved>(status));
        var command = new RequestCoverageReviewCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1));
        (await new RequestCoverageReviewCommandHandler(store).Handle(command, CancellationToken.None)).Status.Should().Be(expected);
    }
}
