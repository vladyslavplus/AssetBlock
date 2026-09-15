using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Entities;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Recommendations.IngestRecommendationEvent;

/// <summary>
/// Accepts well-formed recommendation beacons. Forged, expired, hidden, self-activity, and duplicate
/// exposures succeed without a write so the response cannot probe the catalog or entitlements.
/// </summary>
internal sealed class IngestRecommendationEventCommandHandler(
    IRecommendationExposureSigner exposureSigner,
    IAssetStore assetStore,
    IRecommendationEventStore recommendationEventStore,
    ILogger<IngestRecommendationEventCommandHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<IngestRecommendationEventCommand, Result>
{
    public async Task<Result> Handle(IngestRecommendationEventCommand command, CancellationToken cancellationToken)
    {
        try
        {
            IngestRecommendationEventRequest request = command.Request;
            DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            if (request.ExpiresAt <= now)
            {
                return Result.Success();
            }

            IReadOnlyList<Guid> candidateIds = request.CandidateIds ?? [];
            var isPersonalVersion = string.Equals(
                request.RankingVersion,
                RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_PERSONAL,
                StringComparison.Ordinal);
            if (isPersonalVersion && command.ActorUserId is null)
            {
                // Personal exposures are audience-bound at issuance; an anonymous
                // caller can never satisfy the binding.
                return Result.Success();
            }

            var payload = new RecommendationExposurePayload(
                request.ExposureId,
                request.SourceAssetId,
                request.RankingVersion,
                request.ExpiresAt,
                candidateIds,
                isPersonalVersion ? command.ActorUserId : null);

            if (!exposureSigner.TryVerify(payload, request.ExposureToken))
            {
                return Result.Success();
            }

            if (request.SlotPosition < 0
                || request.SlotPosition >= candidateIds.Count
                || candidateIds[request.SlotPosition] != request.TargetAssetId)
            {
                return Result.Success();
            }

            Guid? sourceSellerId = await assetStore.GetPublicAnalyticsSellerId(request.SourceAssetId, cancellationToken);
            Guid? targetSellerId = await assetStore.GetPublicAnalyticsSellerId(request.TargetAssetId, cancellationToken);
            if (sourceSellerId is null || targetSellerId is null)
            {
                return Result.Success();
            }

            if (command.ActorUserId is { } actor
                && (actor == sourceSellerId.Value || actor == targetSellerId.Value))
            {
                return Result.Success();
            }

            var row = new RecommendationEvent
            {
                Id = request.EventId,
                EventType = request.EventType,
                OccurredAt = now,
                ExposureId = request.ExposureId,
                SourceAssetId = request.SourceAssetId,
                TargetAssetId = request.TargetAssetId,
                SlotPosition = request.SlotPosition,
                RankingVersion = request.RankingVersion,
                VisitorId = request.VisitorId,
                SessionId = request.SessionId,
                ActorUserId = command.ActorUserId,
                DeviceClass = request.DeviceClass
            };

            await recommendationEventStore.TryInsert(row, cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to record recommendation event {RecommendationEventId} of type {RecommendationEventType}",
                command.Request.EventId,
                command.Request.EventType);
            return Result.Success();
        }
    }
}
