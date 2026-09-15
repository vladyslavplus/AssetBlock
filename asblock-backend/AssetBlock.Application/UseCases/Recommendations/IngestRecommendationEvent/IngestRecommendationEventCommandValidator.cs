using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Recommendations.IngestRecommendationEvent;

/// <summary>
/// Validates the beacon envelope only. Forged tokens, expiry, and hidden targets are not 400s.
/// </summary>
internal sealed class IngestRecommendationEventCommandValidator : AbstractValidator<IngestRecommendationEventCommand>
{
    public IngestRecommendationEventCommandValidator()
    {
        RuleFor(c => c.Request.EventId)
            .NotEmpty()
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'eventId' is required.");

        RuleFor(c => c.Request.VisitorId)
            .NotEmpty()
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'visitorId' is required.");

        RuleFor(c => c.Request.SessionId)
            .NotEmpty()
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'sessionId' is required.");

        RuleFor(c => c.Request.EventType)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'eventType' is invalid.");

        RuleFor(c => c.Request.SourceAssetId)
            .NotEmpty()
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'sourceAssetId' is required.");

        RuleFor(c => c.Request.TargetAssetId)
            .NotEmpty()
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'targetAssetId' is required.");

        RuleFor(c => c.Request.SlotPosition)
            .InclusiveBetween(RecommendationTelemetryConstants.SLOT_MIN, RecommendationTelemetryConstants.SLOT_MAX)
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'slotPosition' is invalid.");

        RuleFor(c => c.Request.ExposureId)
            .NotEmpty()
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'exposureId' is required.");

        RuleFor(c => c.Request.RankingVersion)
            .NotEmpty()
            .MaximumLength(RecommendationTelemetryConstants.RANKING_VERSION_MAX_LENGTH)
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'rankingVersion' is invalid.");

        RuleFor(c => c.Request.ExpiresAt)
            .Must(value => value != default)
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'expiresAt' is required.");

        RuleFor(c => c.Request.ExposureToken)
            .NotEmpty()
            .Length(RecommendationTelemetryConstants.EXPOSURE_TOKEN_LENGTH)
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'exposureToken' is invalid.");

        RuleFor(c => c.Request.CandidateIds)
            .NotNull()
            .Must(ids => ids is { Count: >= 1 and <= SimilarAssetsConstants.MAX_LIMIT }
                && ids.All(id => id != Guid.Empty))
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'candidateIds' is invalid.");

        RuleFor(c => c.Request.DeviceClass)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID + ": 'deviceClass' is invalid.");
    }
}
