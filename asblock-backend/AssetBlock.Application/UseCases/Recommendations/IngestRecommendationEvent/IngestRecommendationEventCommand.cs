using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Recommendations;

namespace AssetBlock.Application.UseCases.Recommendations.IngestRecommendationEvent;

/// <summary>
/// Records one recommendation impression or click. ActorUserId is null for anonymous visitors.
/// Success means the envelope was accepted, not that a row was written.
/// </summary>
public sealed record IngestRecommendationEventCommand(
    IngestRecommendationEventRequest Request,
    Guid? ActorUserId) : IRequest<Result>;
