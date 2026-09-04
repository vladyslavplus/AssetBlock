using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Analytics;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Analytics.IngestAnalyticsEvent;

/// <summary>
/// Resolves the seller that owns the beacon target and appends one telemetry row.
/// Every well-formed envelope succeeds: a hidden target, an unauthorized download, a seller viewing
/// their own product, or a replayed event id all resolve to "accepted, nothing written", so the
/// response can never be used to probe catalog contents or entitlements.
/// </summary>
internal sealed class IngestAnalyticsEventCommandHandler(
    IAssetStore assetStore,
    IBundleStore bundleStore,
    ICollectionStore collectionStore,
    IAnalyticsEventStore analyticsEventStore,
    ILogger<IngestAnalyticsEventCommandHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<IngestAnalyticsEventCommand, Result>
{
    public async Task<Result> Handle(IngestAnalyticsEventCommand request, CancellationToken cancellationToken)
    {
        try
        {
            Guid? sellerId = await ResolveSellerId(request, cancellationToken);
            if (sellerId is null || sellerId == request.ActorUserId)
            {
                return Result.Success();
            }

            DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            await analyticsEventStore.TryInsert(BuildEvent(request.Request, sellerId.Value, request.ActorUserId, now), cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Telemetry is best-effort: a storage failure must not surface on a page view or a download.
            logger.LogError(
                ex,
                "Failed to record analytics event {AnalyticsEventId} of type {AnalyticsEventType}",
                request.Request.EventId,
                request.Request.EventType);
            return Result.Success();
        }
    }

    /// <summary>Returns the owning seller when the target is countable for this actor, otherwise null.</summary>
    private async Task<Guid?> ResolveSellerId(IngestAnalyticsEventCommand command, CancellationToken cancellationToken)
    {
        IngestAnalyticsEventRequest request = command.Request;

        switch (request.EventType)
        {
            case AnalyticsEventType.ASSET_VIEW:
                return await assetStore.GetPublicAnalyticsSellerId(request.AssetId!.Value, cancellationToken);

            case AnalyticsEventType.BUNDLE_VIEW:
                return await bundleStore.GetPublicAnalyticsSellerId(request.BundleId!.Value, cancellationToken);

            case AnalyticsEventType.COLLECTION_VIEW:
                return await collectionStore.GetPublishedSellerId(request.CollectionId!.Value, cancellationToken);

            case AnalyticsEventType.COLLECTION_ITEM_CLICK:
                return await collectionStore.GetPublishedMemberSellerId(
                    request.CollectionId!.Value,
                    request.AssetId!.Value,
                    cancellationToken);

            case AnalyticsEventType.DOWNLOAD_REQUESTED:
                return await ResolveDownloadSellerId(command, cancellationToken);

            default:
                return null;
        }
    }

    private Task<Guid?> ResolveDownloadSellerId(
        IngestAnalyticsEventCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ActorUserId is not { } actorUserId)
        {
            return Task.FromResult<Guid?>(null);
        }

        IngestAnalyticsEventRequest request = command.Request;
        return assetStore.ResolveDownloadAnalyticsSellerId(
            request.AssetId!.Value,
            request.AssetVersionId!.Value,
            actorUserId,
            cancellationToken);
    }

    private static AnalyticsEvent BuildEvent(IngestAnalyticsEventRequest request, Guid sellerId, Guid? actorUserId, DateTimeOffset now)
    {
        // A referrer host is only meaningful for external traffic, and an unparseable one is dropped
        // rather than stored raw so no path or query fragment reaches the database.
        var referrerHost = request.Source == AnalyticsTrafficSource.EXTERNAL
            ? AnalyticsReferrerHost.Normalize(request.ReferrerHost)
            : null;

        return new AnalyticsEvent
        {
            Id = request.EventId,
            EventType = request.EventType,
            OccurredAt = now,
            SellerId = sellerId,
            VisitorId = request.VisitorId,
            SessionId = request.SessionId,
            ActorUserId = actorUserId,
            AssetId = request.AssetId,
            AssetVersionId = request.AssetVersionId,
            BundleId = request.BundleId,
            CollectionId = request.CollectionId,
            Source = request.Source,
            ReferrerHost = referrerHost,
            DeviceClass = request.DeviceClass
        };
    }
}
