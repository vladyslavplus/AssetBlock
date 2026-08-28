using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class OutboxEmailDeliveryConfiguration : IEntityTypeConfiguration<OutboxEmailDelivery>
{
    public void Configure(EntityTypeBuilder<OutboxEmailDelivery> builder)
    {
        builder.ToTable("outbox_email_deliveries");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.OutboxMessageId).IsRequired();
        builder.Property(d => d.MessageId).IsRequired().HasMaxLength(256);
        builder.Property(d => d.RecipientAddress).IsRequired().HasMaxLength(256);
        builder.Property(d => d.RecipientUserId).IsRequired();
        builder.Property(d => d.TemplateKind).IsRequired();
        builder.Property(d => d.ClaimToken);
        builder.Property(d => d.ClaimedUntil);
        builder.Property(d => d.DeliveredAt);

        builder.HasIndex(d => d.OutboxMessageId)
            .IsUnique()
            .HasDatabaseName("IX_outbox_email_deliveries_OutboxMessageId");

        builder.HasIndex(d => d.MessageId)
            .IsUnique()
            .HasDatabaseName("IX_outbox_email_deliveries_MessageId");
    }
}
