namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Pushes user-targeted real-time notifications (e.g., SignalR). Implementations must not break
/// the calling use case if delivery fails (log and continue).
/// </summary>
public interface IRealtimeNotificationPublisher
{
    Task NotifyPurchaseCompleted(Guid userId, Guid assetId, string assetTitle, CancellationToken cancellationToken = default);

    Task NotifyDownloadReady(Guid userId, Guid assetId, string assetTitle, CancellationToken cancellationToken = default);

    Task NotifyAssetSold(Guid authorUserId, Guid assetId, string assetTitle, Guid buyerUserId, CancellationToken cancellationToken = default);

    Task NotifyReviewReceived(Guid authorUserId, Guid assetId, string assetTitle, Guid reviewerUserId, int rating, CancellationToken cancellationToken = default);
}
