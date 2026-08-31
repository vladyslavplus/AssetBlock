using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Outbox;

/// <summary>OrderCompleted is recorded for auditing / future consumers; notifications are separate outbox rows.</summary>
internal sealed class OrderCompletedOutboxHandler(
    ILogger<OrderCompletedOutboxHandler> logger) : IOutboxMessageHandler
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string MessageType => OutboxMessageTypes.ORDER_COMPLETED;

    public Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        OrderCompletedPayload payload = JsonSerializer.Deserialize<OrderCompletedPayload>(message.Payload, _json)
                      ?? throw new InvalidOperationException("Invalid OrderCompleted payload.");

        logger.LogInformation(
            "OrderCompleted outbox processed: Order {OrderId}, User {UserId}, Asset {AssetId}, Bundle {BundleId}, Items {ItemCount}",
            payload.OrderId,
            payload.UserId,
            payload.AssetId,
            payload.BundleId,
            payload.ItemCount);
        return Task.CompletedTask;
    }
}
