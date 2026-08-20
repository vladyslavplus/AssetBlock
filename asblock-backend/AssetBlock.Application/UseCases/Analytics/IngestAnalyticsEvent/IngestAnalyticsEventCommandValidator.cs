using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Analytics.IngestAnalyticsEvent;

/// <summary>
/// Validates the beacon envelope only. Whether the target exists or is visible is deliberately not
/// validated here, because a 400 for an unknown id would let a caller enumerate the catalog.
/// </summary>
internal sealed class IngestAnalyticsEventCommandValidator : AbstractValidator<IngestAnalyticsEventCommand>
{
    public IngestAnalyticsEventCommandValidator()
    {
        RuleFor(c => c.Request.EventId)
            .NotEmpty()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_EVENT_INVALID + ": 'eventId' is required.");

        RuleFor(c => c.Request.VisitorId)
            .NotEmpty()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_EVENT_INVALID + ": 'visitorId' is required.");

        RuleFor(c => c.Request.SessionId)
            .NotEmpty()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_EVENT_INVALID + ": 'sessionId' is required.");

        RuleFor(c => c.Request.EventType)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_EVENT_INVALID + ": 'eventType' is invalid.");

        RuleFor(c => c.Request.Source)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_EVENT_INVALID + ": 'source' is invalid.");

        RuleFor(c => c.Request.DeviceClass)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_EVENT_INVALID + ": 'deviceClass' is invalid.");

        RuleFor(c => c.Request.ReferrerHost)
            .MaximumLength(AnalyticsTelemetryConstants.REFERRER_HOST_MAX_LENGTH)
            .WithMessage(ErrorCodes.ERR_ANALYTICS_EVENT_INVALID + ": 'referrerHost' is too long.");

        RuleFor(c => c.Request)
            .Must(HasValidTargetShape)
            .WithMessage(ErrorCodes.ERR_ANALYTICS_EVENT_INVALID + ": target ids do not match 'eventType'.");
    }

    /// <summary>Mirrors the analytics_events target-shape check constraint.</summary>
    private static bool HasValidTargetShape(IngestAnalyticsEventRequest request)
    {
        var hasAsset = IsPresent(request.AssetId);
        var hasVersion = IsPresent(request.AssetVersionId);
        var hasBundle = IsPresent(request.BundleId);
        var hasCollection = IsPresent(request.CollectionId);

        return request.EventType switch
        {
            AnalyticsEventType.ASSET_VIEW => hasAsset && !hasVersion && !hasBundle && !hasCollection,
            AnalyticsEventType.BUNDLE_VIEW => hasBundle && !hasAsset && !hasVersion && !hasCollection,
            AnalyticsEventType.COLLECTION_VIEW => hasCollection && !hasAsset && !hasVersion && !hasBundle,
            AnalyticsEventType.COLLECTION_ITEM_CLICK => hasCollection && hasAsset && !hasVersion && !hasBundle,
            AnalyticsEventType.DOWNLOAD_REQUESTED => hasAsset && hasVersion && !hasBundle && !hasCollection,
            _ => false
        };
    }

    private static bool IsPresent(Guid? id) => id is { } value && value != Guid.Empty;
}
