using AssetBlock.Application.UseCases.Moderation.RequestAppeal;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Moderation;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Moderation.RequestAppeal;

public sealed class RequestAppealCommandValidatorTests
{
    [Theory]
    [InlineData("owner")]
    [InlineData("asset")]
    [InlineData("version")]
    [InlineData("workspace")]
    [InlineData("operation")]
    [InlineData("workspaceRevision")]
    [InlineData("caseRevision")]
    [InlineData("nullRationale")]
    [InlineData("emptyRationale")]
    [InlineData("longRationale")]
    [InlineData("nulRationale")]
    public void Validate_WhenInputIsInvalid_ShouldReject(string field)
    {
        RequestAppealCommand valid = NewCommand();
        RequestAppealCommand input = field switch
        {
            "owner" => valid with { OwnerId = Guid.Empty },
            "asset" => valid with { AssetId = Guid.Empty },
            "version" => valid with { AssetVersionId = Guid.Empty },
            "workspace" => valid with { WorkspaceId = Guid.Empty },
            "operation" => valid with { OperationId = Guid.Empty },
            "workspaceRevision" => valid with { ExpectedWorkspaceRevision = 0 },
            "caseRevision" => valid with { ExpectedCaseRevision = 0 },
            "nullRationale" => valid with { Rationale = null },
            "emptyRationale" => valid with { Rationale = "   " },
            "longRationale" => valid with { Rationale = new string('a', 2001) },
            _ => valid with { Rationale = "invalid\0text" }
        };
        new RequestAppealCommandValidator().Validate(input).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenRationaleHasPadding_ShouldMeasureTrimmedText()
    {
        new RequestAppealCommandValidator().Validate(NewCommand() with { Rationale = "  " + new string('x', 2000) + "  " }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRawRationaleExceedsLimit_ShouldRejectEvenWhenTrimmedIsShort()
    {
        new RequestAppealCommandValidator().Validate(NewCommand() with { Rationale = new string(' ', 4001) + "x" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WhenAppealCreated_ShouldNormalizeAndReturnIdentity()
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        var expected = Guid.NewGuid();
        store.RequestAppeal(Arg.Any<AppealCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GuardedSubmissionResult(GuardedSubmissionStatus.CREATED, expected, null));
        RequestAppealCommand command = NewCommand() with { Rationale = "  Review this decision.  " };
        Ardalis.Result.Result<Guid> result = await new RequestAppealCommandHandler(store).Handle(command, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
        await store.Received(1).RequestAppeal(Arg.Is<AppealCreateRequest>(r => r.Rationale == "Review this decision." && r.RequestDigest.Length == 64), Arg.Any<CancellationToken>());
    }

    private static RequestAppealCommand NewCommand() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1, Guid.NewGuid(), "Review this decision.");
}
