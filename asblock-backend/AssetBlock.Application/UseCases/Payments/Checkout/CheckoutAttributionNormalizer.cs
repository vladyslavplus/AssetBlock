using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Analytics;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Payments.Checkout;

/// <summary>Verified attribution captured once when a checkout intent is created.</summary>
internal sealed record CheckoutAttributionSnapshot(
    Guid? VisitorId,
    Guid? SessionId,
    AnalyticsTrafficSource Source,
    Guid? CollectionId,
    string? ReferrerHost);

/// <summary>
/// Reduces an untrusted attribution hint to a snapshot the checkout_intents constraints accept.
/// Anything unverifiable is dropped in full rather than partially trusted, and no outcome is an error:
/// attribution is a reporting nicety and must never block a purchase.
/// </summary>
internal sealed class CheckoutAttributionNormalizer(
    ICollectionStore collectionStore,
    ILogger<CheckoutAttributionNormalizer> logger)
{
    /// <summary>
    /// Returns the snapshot to persist, or null when the hint is absent or cannot be trusted.
    /// COLLECTION attribution is only accepted for a single-asset checkout whose asset is a publicly
    /// visible member of a published collection owned by the same seller.
    /// </summary>
    public async Task<CheckoutAttributionSnapshot?> TryNormalize(
        CheckoutAttributionRequest? request,
        Guid? assetId,
        Guid sellerId,
        Guid? visitorId = null,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request?.Source is not { } source || !Enum.IsDefined(source))
            {
                return null;
            }

            visitorId = NormalizeGuid(visitorId);
            sessionId = NormalizeGuid(sessionId);

            if (source == AnalyticsTrafficSource.COLLECTION)
            {
                if (assetId is not { } attributedAssetId || request.CollectionId is not { } rawCollectionId)
                {
                    return null;
                }

                var collectionId = NormalizeGuid(rawCollectionId);
                if (collectionId is not { } verifiedCollectionId)
                {
                    return null;
                }

                var collectionSellerId = await collectionStore.GetPublishedMemberSellerId(
                    verifiedCollectionId,
                    attributedAssetId,
                    cancellationToken);
                if (collectionSellerId != sellerId)
                {
                    return null;
                }

                return new CheckoutAttributionSnapshot(visitorId, sessionId, source, verifiedCollectionId, ReferrerHost: null);
            }

            if (request.CollectionId is not null)
            {
                return null;
            }

            var referrerHost = source == AnalyticsTrafficSource.EXTERNAL
                ? AnalyticsReferrerHost.Normalize(request.ReferrerHost)
                : null;

            return new CheckoutAttributionSnapshot(visitorId, sessionId, source, CollectionId: null, referrerHost);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to normalize checkout attribution; dropping snapshot");
            return null;
        }
    }

    private static Guid? NormalizeGuid(Guid? value)
    {
        if (value is null || value == Guid.Empty)
        {
            return null;
        }

        return value;
    }

    private static Guid? NormalizeGuid(Guid value) => value == Guid.Empty ? null : value;
}
