using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Type).IsRequired().HasMaxLength(128);
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.OccurredAt).IsRequired();
        builder.Property(m => m.AttemptCount).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(2000);

        builder.HasIndex(m => new { m.ProcessedAt, m.NextAttemptAt, m.LockedUntil, m.OccurredAt })
            .HasDatabaseName("IX_outbox_messages_dispatch");
    }
}
