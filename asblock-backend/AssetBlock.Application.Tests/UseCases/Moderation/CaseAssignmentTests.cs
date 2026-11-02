using Ardalis.Result;
using AssetBlock.Application.UseCases.Moderation.CaseAssignment;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Moderation;

public sealed class CaseAssignmentTests
{
    [Theory]
    [InlineData("actor")]
    [InlineData("submission")]
    [InlineData("operation")]
    [InlineData("revision")]
    public void Validate_WhenOperationIdentityOrRevisionIsInvalid_ShouldReject(string field)
    {
        var validator = new ChangeCaseClaimCommandValidator();
        var command = new ChangeCaseClaimCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, true);
        command = field switch { "actor" => command with { ActorId = Guid.Empty }, "submission" => command with { SubmissionId = Guid.Empty }, "operation" => command with { OperationId = Guid.Empty }, _ => command with { ExpectedCaseRevision = 0 } };
        validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(AppRoles.USER)]
    [InlineData(AppRoles.ADMIN)]
    [InlineData(AppRoles.MODERATOR)]
    [InlineData("unknown")]
    [InlineData(null)]
    public void RolePredicate_WhenEvaluated_ShouldAgreeWithExpression(string? role)
    {
        AppRoles.CanReviewExpression.Compile()(role).Should().Be(AppRoles.CanReview(role));
    }
    [Theory]
    [InlineData(CaseClaimStatus.NOT_CLAIMED, ResultStatus.Conflict, ErrorCodes.ERR_MODERATION_CASE_NOT_CLAIMED)]
    [InlineData(CaseClaimStatus.NOT_ACTIVE, ResultStatus.Conflict, ErrorCodes.ERR_MODERATION_CASE_NOT_ACTIVE)]
    [InlineData(CaseClaimStatus.AUDIT_UNAVAILABLE, ResultStatus.Unavailable, ErrorCodes.ERR_REVIEW_AUDIT_UNAVAILABLE)]
    public async Task Handle_WhenStoreReturnsFailure_ShouldPreserveReason(CaseClaimStatus status, ResultStatus expected, string code)
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        IUserStore users = Substitute.For<IUserStore>();
        var actor = Guid.NewGuid();
        users.GetPersistedRole(actor, Arg.Any<CancellationToken>()).Returns(new UserPersistedRole(actor, AppRoles.MODERATOR, 1));
        store.ChangeCaseClaim(Arg.Any<CaseClaimChange>(), Arg.Any<CancellationToken>()).Returns(new CaseClaimResult(status));
        Result<CaseClaimSaved> result = await new ChangeCaseClaimCommandHandler(store, users).Handle(new(actor, Guid.NewGuid(), Guid.NewGuid(), 1, false), CancellationToken.None);
        result.Status.Should().Be(expected);
        result.Errors.Should().Contain(code);
    }

    [Theory]
    [InlineData(AppRoles.USER)]
    [InlineData(AppRoles.ADMIN)]
    public async Task Handle_WhenNotModerator_ShouldDenyBeforeStore(string role)
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        IUserStore users = Substitute.For<IUserStore>();
        var actor = Guid.NewGuid();
        users.GetPersistedRole(actor, Arg.Any<CancellationToken>()).Returns(new UserPersistedRole(actor, role, 1));
        var command = new ChangeCaseClaimCommand(actor, Guid.NewGuid(), Guid.NewGuid(), 1, true);
        Result<CaseClaimSaved> result = await new ChangeCaseClaimCommandHandler(store, users).Handle(command, CancellationToken.None);
        result.Status.Should().Be(ResultStatus.Forbidden);
        store.ReceivedCalls().Should().BeEmpty();
    }
}
