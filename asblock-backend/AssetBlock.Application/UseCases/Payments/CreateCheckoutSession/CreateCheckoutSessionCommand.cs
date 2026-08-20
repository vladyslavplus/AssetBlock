using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;

/// <summary>
/// Analytics arguments are optional hints; they are only persisted when a new intent is created.
/// </summary>
public sealed record CreateCheckoutSessionCommand(
    Guid AssetId,
    Guid UserId,
    CheckoutAttributionRequest? Attribution = null,
    Guid? AnalyticsVisitorId = null,
    Guid? AnalyticsSessionId = null) : IRequest<Result<CreateCheckoutSessionResponse>>;
