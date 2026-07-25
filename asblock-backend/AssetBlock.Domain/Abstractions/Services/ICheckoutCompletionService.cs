using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Payments;

namespace AssetBlock.Domain.Abstractions.Services;

public interface ICheckoutCompletionService
{
    Task<OrderCompletedPayload?> CompletePaidCheckout(
        StripeCheckoutCompleted checkout,
        CancellationToken cancellationToken = default);
}
