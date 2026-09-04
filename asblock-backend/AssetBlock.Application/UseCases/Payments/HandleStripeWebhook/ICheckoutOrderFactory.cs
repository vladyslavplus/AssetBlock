using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal interface ICheckoutOrderFactory
{
    (Order Order, IReadOnlyList<OrderLine> Lines, IReadOnlyList<Purchase> Purchases) CreateOrderWithPurchases(
        Guid orderId,
        CheckoutIntent checkoutIntent,
        IReadOnlyList<CheckoutIntentItem> items,
        StripeCheckoutCompleted verified,
        DateTimeOffset purchasedAt);
}
