using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
    public void Configure(EntityTypeBuilder<UserNotification> builder)
    {
        builder.ToTable("user_notifications");

        builder.HasKey(n => n.Id);
        builder.Property(n => n.CreatedAt).IsRequired();
        builder.Property(n => n.UpdatedAt);
        builder.Property(n => n.RecipientUserId).IsRequired();
        builder.Property(n => n.Kind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(64);
        builder.Property(n => n.MetadataJson)
            .IsRequired()
            .HasMaxLength(NotificationConstraints.MAX_METADATA_JSON_LENGTH);
        builder.Property(n => n.ReadAt);

        builder.HasIndex(n => new { n.RecipientUserId, n.CreatedAt });

        builder.HasOne(n => n.Recipient)
            .WithMany()
            .HasForeignKey(n => n.RecipientUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
