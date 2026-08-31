using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Analytics;

namespace AssetBlock.Application.UseCases.Analytics.IngestAnalyticsEvent;

/// <summary>
/// Records one engagement beacon. ActorUserId is null for anonymous visitors. Success means the
/// envelope was accepted, not that a row was written; suppressed events also succeed.
/// </summary>
public sealed record IngestAnalyticsEventCommand(
    IngestAnalyticsEventRequest Request,
    Guid? ActorUserId) : IRequest<Result>;
