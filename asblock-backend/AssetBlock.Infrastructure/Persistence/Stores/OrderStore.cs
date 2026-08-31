using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class OrderStore(ApplicationDbContext dbContext) : IOrderStore
{
    private const string UNIQUE_STRIPE_SESSION = "UIX_orders_stripe_session";
    private const string UNIQUE_CHECKOUT_INTENT = "UIX_orders_checkout_intent";

    public Task<Order?> GetByStripeSessionId(string stripeSessionId, CancellationToken cancellationToken = default)
    {
        return dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.StripeSessionId == stripeSessionId, cancellationToken);
    }

    public Task<Order?> GetByCheckoutIntentId(Guid checkoutIntentId, CancellationToken cancellationToken = default)
    {
        return dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.CheckoutIntentId == checkoutIntentId, cancellationToken);
    }

    public Task<Order?> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public async Task<Order> CreateWithLinesAndPurchases(
        Order order,
        IReadOnlyList<OrderLine> lines,
        IReadOnlyList<Purchase> purchases,
        CancellationToken cancellationToken = default)
    {
        try
        {
            dbContext.Orders.Add(order);
            dbContext.OrderLines.AddRange(lines);
            dbContext.Purchases.AddRange(purchases);
            await dbContext.SaveChangesAsync(cancellationToken);
            return order;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: UNIQUE_STRIPE_SESSION or UNIQUE_CHECKOUT_INTENT
            })
        {
            DetachGraph(order, lines, purchases);
            throw new DuplicateOrderException();
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: PurchaseConfiguration.UNIQUE_USER_ASSET
                    or PurchaseConfiguration.UNIQUE_ORDER_LINE
            })
        {
            DetachGraph(order, lines, purchases);
            throw new DuplicateEntitlementException();
        }
    }

    private void DetachGraph(Order order, IReadOnlyList<OrderLine> lines, IReadOnlyList<Purchase> purchases)
    {
        foreach (Purchase purchase in purchases)
        {
            dbContext.Entry(purchase).State = EntityState.Detached;
        }

        foreach (OrderLine line in lines)
        {
            dbContext.Entry(line).State = EntityState.Detached;
        }

        dbContext.Entry(order).State = EntityState.Detached;
    }
}
