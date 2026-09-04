using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal interface ICheckoutNotificationPublisher
{
    Task EnqueueOrderCompletionSideEffects(
        Order order,
        IReadOnlyList<OrderLine> lines,
        EmailRecipient? buyer,
        EmailRecipient? sellerRecipient,
        Guid sellerId,
        Guid buyerUserId,
        DateTimeOffset purchasedAt,
        CancellationToken cancellationToken = default);
}
