using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class EmailActionConfiguration : IEntityTypeConfiguration<EmailAction>
{
    public void Configure(EntityTypeBuilder<EmailAction> builder)
    {
        builder.ToTable("email_actions", table =>
        {
            table.HasCheckConstraint(
                "CK_email_actions_ExpiresAt_After_CreatedAt",
                "\"ExpiresAt\" > \"CreatedAt\"");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.UserId).IsRequired();
        builder.Property(a => a.Purpose)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(64);
        builder.Property(a => a.TargetEmail).IsRequired().HasMaxLength(256);
        builder.Property(a => a.Version).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.ExpiresAt).IsRequired();
        builder.Property(a => a.ConsumedAt);
        builder.Property(a => a.LastSentAt);

        builder.HasOne(a => a.User)
            .WithMany(u => u.EmailActions)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.UserId, a.Purpose })
            .IsUnique()
            .HasDatabaseName("IX_email_actions_UserId_Purpose");

        builder.HasIndex(a => a.ExpiresAt)
            .HasDatabaseName("IX_email_actions_ExpiresAt");
    }
}
