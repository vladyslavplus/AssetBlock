using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Outbox;
using MediatR;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

public sealed record HandleStripeWebhookCommand(string Payload, string Signature)
    : IRequest<Result<OrderCompletedPayload?>>;
