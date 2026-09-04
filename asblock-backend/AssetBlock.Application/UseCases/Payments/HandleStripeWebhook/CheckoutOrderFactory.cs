using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal sealed class CheckoutOrderFactory : ICheckoutOrderFactory
{
    public (Order Order, IReadOnlyList<OrderLine> Lines, IReadOnlyList<Purchase> Purchases) CreateOrderWithPurchases(
        Guid orderId,
        CheckoutIntent checkoutIntent,
        IReadOnlyList<CheckoutIntentItem> items,
        StripeCheckoutCompleted verified,
        DateTimeOffset purchasedAt)
    {
        var lines = new List<OrderLine>(items.Count);
        var purchases = new List<Purchase>(items.Count);

        foreach (CheckoutIntentItem item in items)
        {
            var lineId = Guid.NewGuid();
            lines.Add(new OrderLine
            {
                Id = lineId,
                OrderId = orderId,
                AssetId = item.AssetId,
                AssetVersionId = item.AssetVersionId,
                SellerId = item.SellerId,
                Position = item.Position,
                AssetTitleSnapshot = item.AssetTitleSnapshot,
                VersionNumber = item.VersionNumber,
                ListPrice = item.ListPrice,
                PricePaid = item.AllocatedPrice,
                LicenseCode = item.LicenseCode,
                LicenseTemplateVersion = item.LicenseTemplateVersion,
                LicenseDisplayName = item.LicenseDisplayName,
                LicenseTerms = item.LicenseTerms
            });

            purchases.Add(new Purchase
            {
                Id = Guid.NewGuid(),
                UserId = verified.UserId,
                AssetId = item.AssetId,
                AssetVersionId = item.AssetVersionId,
                OrderLineId = lineId,
                PurchasedAt = purchasedAt
            });
        }

        var order = new Order
        {
            Id = orderId,
            UserId = verified.UserId,
            CheckoutIntentId = verified.CheckoutIntentId,
            AssetId = checkoutIntent.AssetId,
            BundleId = checkoutIntent.BundleId,
            BundleRevisionId = checkoutIntent.BundleRevisionId,
            ProductTitle = checkoutIntent.ProductTitle,
            StripeSessionId = verified.StripeSessionId,
            AmountPaid = verified.AmountTotal,
            Currency = verified.Currency,
            PurchasedAt = purchasedAt,
            Lines = lines
        };

        return (order, lines, purchases);
    }
}
