using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>Durable ledger record for external email deliveries to enforce outbox idempotency.</summary>
public sealed class OutboxEmailDelivery
{
    public required Guid Id { get; init; }
    public required Guid OutboxMessageId { get; init; }
    public required string MessageId { get; init; }
    public required string RecipientAddress { get; init; }
    public required Guid RecipientUserId { get; init; }
    public required EmailTemplateKind TemplateKind { get; init; }
    public Guid? ClaimToken { get; set; }
    public DateTimeOffset? ClaimedUntil { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
