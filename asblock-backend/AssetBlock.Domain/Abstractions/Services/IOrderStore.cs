using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IOrderStore
{
    Task<Order?> GetByStripeSessionId(string stripeSessionId, CancellationToken cancellationToken = default);
    Task<Order?> GetByCheckoutIntentId(Guid checkoutIntentId, CancellationToken cancellationToken = default);
    Task<Order?> GetById(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates order, lines, and entitlements. Caller owns the surrounding transaction.
    /// </summary>
    Task<Order> CreateWithLinesAndPurchases(
        Order order,
        IReadOnlyList<OrderLine> lines,
        IReadOnlyList<Purchase> purchases,
        CancellationToken cancellationToken = default);
}
