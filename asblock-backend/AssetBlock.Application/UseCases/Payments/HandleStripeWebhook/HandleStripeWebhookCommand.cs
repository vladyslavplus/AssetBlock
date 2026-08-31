using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Outbox;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

public sealed record HandleStripeWebhookCommand(string Payload, string Signature)
    : IRequest<Result<OrderCompletedPayload?>>;
