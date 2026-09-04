namespace AssetBlock.Domain.Core.Entities;

/// <summary>Durable ledger record for processed Stripe webhook events to guarantee at-most-once processing.</summary>
public sealed class ProcessedStripeWebhookEvent
{
    public required Guid Id { get; init; }
    public required string StripeEventId { get; init; }
    public required string EventType { get; init; }
    public required DateTimeOffset ProcessedAt { get; init; }
}
