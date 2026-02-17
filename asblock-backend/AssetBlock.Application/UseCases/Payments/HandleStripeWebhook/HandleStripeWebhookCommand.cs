using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

public sealed record HandleStripeWebhookCommand(string Payload, string Signature) : IRequest<Result<PurchaseCompletedPayload?>>;

public sealed record PurchaseCompletedPayload(Guid UserId, Guid AssetId);
