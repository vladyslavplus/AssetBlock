using AssetBlock.Application.UseCases.Moderation.GetModerationSubmissionCase;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Moderation;

public sealed class GetModerationSubmissionCaseQueryHandlerTests
{
    private readonly IModerationFoundationStore _moderationStore = Substitute.For<IModerationFoundationStore>();
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly GetModerationSubmissionCaseQueryHandler _handler;

    public GetModerationSubmissionCaseQueryHandlerTests()
    {
        _handler = new GetModerationSubmissionCaseQueryHandler(_moderationStore, _userStore);
    }

    [Fact]
    public async Task Handle_WhenActorIsAdminNotModerator_ShouldForbidden()
    {
        var actorId = Guid.NewGuid();
        _userStore.GetPersistedRole(actorId, Arg.Any<CancellationToken>())
            .Returns(new UserPersistedRole(actorId, AppRoles.ADMIN, 1));

        Ardalis.Result.Result<ModerationCaseSummary> result = await _handler.Handle(
            new GetModerationSubmissionCaseQuery(actorId, Guid.NewGuid()),
            CancellationToken.None);

        result.Status.Should().Be(Ardalis.Result.ResultStatus.Forbidden);
        await _moderationStore.DidNotReceive().GetCaseSummaryForModerator(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenModeratorAndCaseExists_ShouldReturnSummary()
    {
        var actorId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        _userStore.GetPersistedRole(actorId, Arg.Any<CancellationToken>())
            .Returns(new UserPersistedRole(actorId, AppRoles.MODERATOR, 2));
        ModerationCaseSummary summary = new(
            submissionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ModerationSubmissionState.SUBMITTED,
            1,
            new string('a', 64),
            "policy-v1");
        _moderationStore.GetCaseSummaryForModerator(actorId, submissionId, Arg.Any<CancellationToken>())
            .Returns(new ModerationCaseAccessResult(ModerationCaseAccessStatus.FOUND, summary));

        Ardalis.Result.Result<ModerationCaseSummary> result = await _handler.Handle(
            new GetModerationSubmissionCaseQuery(actorId, submissionId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(summary);
    }

    [Fact]
    public async Task Handle_WhenModeratorOwnsAsset_ShouldForbidden()
    {
        var actorId = Guid.NewGuid();
        _userStore.GetPersistedRole(actorId, Arg.Any<CancellationToken>())
            .Returns(new UserPersistedRole(actorId, AppRoles.MODERATOR, 2));
        _moderationStore.GetCaseSummaryForModerator(actorId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationCaseAccessResult(ModerationCaseAccessStatus.SELF_OWNED_DENIED, null));

        Ardalis.Result.Result<ModerationCaseSummary> result = await _handler.Handle(
            new GetModerationSubmissionCaseQuery(actorId, Guid.NewGuid()),
            CancellationToken.None);

        result.Status.Should().Be(Ardalis.Result.ResultStatus.Forbidden);
    }
}
