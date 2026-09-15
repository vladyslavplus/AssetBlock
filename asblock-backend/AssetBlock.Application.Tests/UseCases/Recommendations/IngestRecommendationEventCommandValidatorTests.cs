using AssetBlock.Application.UseCases.Recommendations.IngestRecommendationEvent;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.UseCases.Recommendations;

public sealed class IngestRecommendationEventCommandValidatorTests
{
    private readonly IngestRecommendationEventCommandValidator _validator = new();

    [Fact]
    public void Validate_WhenEnvelopeIsWellFormed_ShouldPass()
    {
        _validator.Validate(Command()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEventIdIsEmpty_ShouldFail()
    {
        IngestRecommendationEventCommand command = Command() with
        {
            Request = Command().Request with { EventId = Guid.Empty }
        };
        ShouldFail(command);
    }

    [Fact]
    public void Validate_WhenCandidateIdsEmpty_ShouldFail()
    {
        IngestRecommendationEventCommand command = Command() with
        {
            Request = Command().Request with { CandidateIds = [] }
        };
        ShouldFail(command);
    }

    [Fact]
    public void Validate_WhenSlotOutOfRange_ShouldFail()
    {
        IngestRecommendationEventCommand command = Command() with
        {
            Request = Command().Request with { SlotPosition = 12 }
        };
        ShouldFail(command);
    }

    [Fact]
    public void Validate_WhenTokenLengthIsWrong_ShouldFail()
    {
        IngestRecommendationEventCommand command = Command() with
        {
            Request = Command().Request with { ExposureToken = "short" }
        };
        ShouldFail(command);
    }

    private static void ShouldFail(IngestRecommendationEventCommand command)
    {
        ValidationResult result = new IngestRecommendationEventCommandValidator().Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.StartsWith(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID, StringComparison.Ordinal));
    }

    private static IngestRecommendationEventCommand Command()
    {
        var targetId = Guid.NewGuid();
        return new IngestRecommendationEventCommand(
            new IngestRecommendationEventRequest(
                Guid.NewGuid(),
                RecommendationEventType.CLICK,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                targetId,
                0,
                Guid.NewGuid(),
                RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
                DateTimeOffset.UtcNow.AddMinutes(10),
                new string('b', RecommendationTelemetryConstants.EXPOSURE_TOKEN_LENGTH),
                [targetId],
                AnalyticsDeviceClass.MOBILE),
            ActorUserId: null);
    }
}
