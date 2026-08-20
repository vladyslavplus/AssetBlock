using Ardalis.Result;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Payments.CreateBundleCheckoutSession;

/// <summary>
/// Analytics arguments are optional hints; they are only persisted when a new intent is created.
/// </summary>
public sealed record CreateBundleCheckoutSessionCommand(
    Guid BundleId,
    Guid UserId,
    CheckoutAttributionRequest? Attribution = null,
    Guid? AnalyticsVisitorId = null,
    Guid? AnalyticsSessionId = null) : IRequest<Result<CreateCheckoutSessionResponse>>;
