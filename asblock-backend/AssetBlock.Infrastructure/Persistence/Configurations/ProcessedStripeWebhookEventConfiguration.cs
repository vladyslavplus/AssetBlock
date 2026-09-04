using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class ProcessedStripeWebhookEventConfiguration : IEntityTypeConfiguration<ProcessedStripeWebhookEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedStripeWebhookEvent> builder)
    {
        builder.ToTable("processed_stripe_webhook_events");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.StripeEventId).IsRequired().HasMaxLength(256);
        builder.Property(e => e.EventType).IsRequired().HasMaxLength(256);
        builder.Property(e => e.ProcessedAt).IsRequired();

        builder.HasIndex(e => e.StripeEventId)
            .IsUnique()
            .HasDatabaseName("IX_processed_stripe_webhook_events_StripeEventId");
    }
}
