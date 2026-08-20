using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Payments.GetCheckoutStatus;

public sealed record GetCheckoutStatusQuery(Guid CheckoutIntentId, Guid UserId)
    : IRequest<Result<GetCheckoutStatusResponse>>;
