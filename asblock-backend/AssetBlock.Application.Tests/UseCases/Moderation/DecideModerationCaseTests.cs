using Ardalis.Result;
using AssetBlock.Application.UseCases.Moderation.DecideModerationCase;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Moderation;

public sealed class DecideModerationCaseTests
{
    [Fact]
    public async Task Handle_WhenStoreRejectsValidation_ShouldReturnInvalid()
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        IUserStore users = Substitute.For<IUserStore>();
        var actor = Guid.NewGuid();
        users.GetPersistedRole(actor, Arg.Any<CancellationToken>()).Returns(new UserPersistedRole(actor, AppRoles.MODERATOR, 1));
        store.DecideModerationCase(Arg.Any<ModerationDecisionRequest>(), Arg.Any<CancellationToken>()).Returns(new ModerationDecisionResult(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_VALIDATION_FAILED));
        var basis = new DecisionBasis(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 1, 1, ReviewAnalysisPolicy.KEY, 0, 1);
        var command = new DecideModerationCaseCommand(actor, Guid.NewGuid(), Guid.NewGuid(), basis, ModerationDecisionOutcome.APPROVE, "Reason", [], null);
        Result<ModerationDecisionSaved> result = await new DecideModerationCaseCommandHandler(store, users).Handle(command, CancellationToken.None);
        result.Status.Should().Be(ResultStatus.Invalid);
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
        var basis = new DecisionBasis(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 1, 1, ReviewAnalysisPolicy.KEY, 0, 1);
        var command = new DecideModerationCaseCommand(actor, Guid.NewGuid(), Guid.NewGuid(), basis, ModerationDecisionOutcome.APPROVE, "Reason", [], null);
        Result<ModerationDecisionSaved> result = await new DecideModerationCaseCommandHandler(store, users).Handle(command, CancellationToken.None);
        result.Status.Should().Be(ResultStatus.Forbidden);
        store.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenStoreBlocksDecision_ShouldPreserveReason()
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        IUserStore users = Substitute.For<IUserStore>();
        var actor = Guid.NewGuid();
        users.GetPersistedRole(actor, Arg.Any<CancellationToken>()).Returns(new UserPersistedRole(actor, AppRoles.MODERATOR, 1));
        store.DecideModerationCase(Arg.Any<ModerationDecisionRequest>(), Arg.Any<CancellationToken>()).Returns(new ModerationDecisionResult(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_MODERATION_DECISION_BLOCKED));
        var basis = new DecisionBasis(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 1, 1, ReviewAnalysisPolicy.KEY, 0, 1);
        var command = new DecideModerationCaseCommand(actor, Guid.NewGuid(), Guid.NewGuid(), basis, ModerationDecisionOutcome.APPROVE, "Reason", [], null);
        Result<ModerationDecisionSaved> result = await new DecideModerationCaseCommandHandler(store, users).Handle(command, CancellationToken.None);
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_MODERATION_DECISION_BLOCKED);
    }

    [Fact]
    public async Task Handle_WhenPayloadChanges_ShouldChangeDigestForSameOperation()
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        IUserStore users = Substitute.For<IUserStore>();
        var actor = Guid.NewGuid();
        users.GetPersistedRole(actor, Arg.Any<CancellationToken>()).Returns(new UserPersistedRole(actor, AppRoles.MODERATOR, 1));
        var digests = new List<string>();
        store.DecideModerationCase(Arg.Do<ModerationDecisionRequest>(r => digests.Add(r.RequestDigest)), Arg.Any<CancellationToken>())
            .Returns(new ModerationDecisionResult(ModerationDecisionStatus.OPERATION_CONFLICT));
        var basis = new DecisionBasis(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 1, 1, ReviewAnalysisPolicy.KEY, 0, 1);
        var command = new DecideModerationCaseCommand(actor, Guid.NewGuid(), Guid.NewGuid(), basis, ModerationDecisionOutcome.APPROVE, "Reason", [], null);
        var handler = new DecideModerationCaseCommandHandler(store, users);
        await handler.Handle(command, CancellationToken.None);
        await handler.Handle(command with { Rationale = "Different reason" }, CancellationToken.None);
        digests.Should().HaveCount(2);
        digests[0].Should().NotBe(digests[1]);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("case")]
    [InlineData("operation")]
    [InlineData("revision")]
    [InlineData("rationale")]
    [InlineData("basis")]
    [InlineData("hash")]
    [InlineData("outcome")]
    [InlineData("plan")]
    [InlineData("dispositions")]
    [InlineData("disposition")]
    [InlineData("dispositionReason")]
    public void Validate_WhenDecisionInputIsInvalid_ShouldReject(string invalid)
    {
        var basis = new DecisionBasis(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 1, 1, ReviewAnalysisPolicy.KEY, 0, 1);
        var command = new DecideModerationCaseCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), basis,
            ModerationDecisionOutcome.APPROVE, "Reason", [], null);
        command = invalid switch
        {
            "actor" => command with { ActorId = Guid.Empty },
            "case" => command with { SubmissionId = Guid.Empty },
            "operation" => command with { OperationId = Guid.Empty },
            "revision" => command with { Basis = basis with { CaseRevision = 0 } },
            "basis" => command with { Basis = null! },
            "hash" => command with { Basis = basis with { ContentSha256 = "bad" } },
            "outcome" => command with { Outcome = (ModerationDecisionOutcome)999 },
            "plan" => command with { ManualPlanCompletionReference = "bad\0reference" },
            "dispositions" => command with { Dispositions = null! },
            "disposition" => command with { Dispositions = [new(Guid.NewGuid(), (FindingDispositionType)999, "Reason")] },
            "dispositionReason" => command with { Dispositions = [new(Guid.NewGuid(), FindingDispositionType.EXPLAINED, " ")] },
            _ => command with { Rationale = " " }
        };
        new DecideModerationCaseCommandValidator().Validate(command).IsValid.Should().BeFalse();
    }
}
