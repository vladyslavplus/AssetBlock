using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Outbox;

/// <summary>PurchaseCompleted is recorded for auditing / future consumers; notifications are separate outbox rows.</summary>
internal sealed class PurchaseCompletedOutboxHandler(
    ILogger<PurchaseCompletedOutboxHandler> logger) : IOutboxMessageHandler
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string MessageType => OutboxMessageTypes.PURCHASE_COMPLETED;

    public Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<PurchaseCompletedPayload>(message.Payload, _json)
            ?? throw new InvalidOperationException("Invalid PurchaseCompleted payload.");

        logger.LogInformation(
            "PurchaseCompleted outbox processed: Purchase {PurchaseId}, User {UserId}, Asset {AssetId}",
            payload.PurchaseId,
            payload.UserId,
            payload.AssetId);
        return Task.CompletedTask;
    }
}
