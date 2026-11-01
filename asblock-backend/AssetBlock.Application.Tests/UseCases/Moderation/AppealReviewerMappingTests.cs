using AssetBlock.Application.UseCases.Moderation.CaseAssignment;
using AssetBlock.Application.UseCases.Moderation.CoverageReview;
using AssetBlock.Application.UseCases.Moderation.DecideModerationCase;
using AssetBlock.Application.UseCases.Moderation.GetModerationSubmissionCase;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Moderation;

public sealed class AppealReviewerMappingTests
{
    [Fact]
    public async Task Handle_WhenReviewerExcluded_ShouldReturnForbiddenReason()
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        IUserStore users = Substitute.For<IUserStore>();
        var actor = Guid.NewGuid();
        users.GetPersistedRole(actor, Arg.Any<CancellationToken>()).Returns(new UserPersistedRole(actor, AppRoles.MODERATOR, 1));
        store.ChangeCaseClaim(Arg.Any<CaseClaimChange>(), Arg.Any<CancellationToken>()).Returns(new CaseClaimResult(CaseClaimStatus.APPEAL_REVIEWER_EXCLUDED));
        Ardalis.Result.Result<CaseClaimSaved> result = await new ChangeCaseClaimCommandHandler(store, users).Handle(new(actor, Guid.NewGuid(), Guid.NewGuid(), 1, true), CancellationToken.None);
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED);
    }
    [Fact]
    public async Task Handle_WhenExcludedDecider_ShouldPreserveForbiddenReason()
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        IUserStore users = Substitute.For<IUserStore>();
        var actor = Guid.NewGuid();
        users.GetPersistedRole(actor, Arg.Any<CancellationToken>()).Returns(new UserPersistedRole(actor, AppRoles.MODERATOR, 1));
        store.DecideModerationCase(Arg.Any<ModerationDecisionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationDecisionResult(ModerationDecisionStatus.FORBIDDEN, ReasonCode: ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED));
        var basis = new DecisionBasis(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 1, 1, ReviewAnalysisPolicy.KEY, 0, 1);
        var command = new DecideModerationCaseCommand(actor, Guid.NewGuid(), Guid.NewGuid(), basis, ModerationDecisionOutcome.APPROVE, "Reason", [], null);
        Ardalis.Result.Result<ModerationDecisionSaved> result = await new DecideModerationCaseCommandHandler(store, users).Handle(command, CancellationToken.None);
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED);
    }
    [Fact]
    public void Map_WhenEvidenceReviewerExcluded_ShouldPreserveForbiddenReason()
    {
        Ardalis.Result.Result<RestrictedEvidenceDto> result = CoverageReviewResults.Map(new ReviewAccessResult<RestrictedEvidenceDto>(ReviewAccessStatus.FORBIDDEN, ReasonCode: ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED), evidenceRead: true);
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED);
    }

    [Fact]
    public async Task Handle_WhenCaseReviewerExcluded_ShouldPreserveForbiddenReason()
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        IUserStore users = Substitute.For<IUserStore>();
        var actor = Guid.NewGuid();
        users.GetPersistedRole(actor, Arg.Any<CancellationToken>()).Returns(new UserPersistedRole(actor, AppRoles.MODERATOR, 1));
        store.GetCaseSummaryForModerator(actor, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationCaseAccessResult(ModerationCaseAccessStatus.APPEAL_REVIEWER_EXCLUDED, null));
        Ardalis.Result.Result<ModerationCaseSummary> result = await new GetModerationSubmissionCaseQueryHandler(store, users).Handle(new(actor, Guid.NewGuid()), CancellationToken.None);
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED);
    }
}
