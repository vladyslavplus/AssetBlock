using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CheckoutIntentConfiguration : IEntityTypeConfiguration<CheckoutIntent>
{
    public void Configure(EntityTypeBuilder<CheckoutIntent> builder)
    {
        builder.ToTable("checkout_intents", table =>
        {
            table.HasCheckConstraint("CK_checkout_intents_unit_amount_positive", "\"UnitAmount\" > 0");
            table.HasCheckConstraint("CK_checkout_intents_expires_after_created", "\"ExpiresAt\" > \"CreatedAt\"");
        });

        builder.HasKey(i => i.Id);
        builder.Property(i => i.UserId).IsRequired();
        builder.Property(i => i.AssetId).IsRequired();
        builder.Property(i => i.AssetVersionId).IsRequired();
        builder.Property(i => i.AssetTitle).IsRequired().HasMaxLength(500);
        builder.Property(i => i.UnitAmount).IsRequired().HasPrecision(18, 2);
        builder.Property(i => i.Currency).IsRequired().HasMaxLength(3);
        builder.Property(i => i.StripeSessionId).HasMaxLength(256);
        builder.Property(i => i.Status)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(
                status => status.ToString(),
                raw => Enum.Parse<CheckoutIntentStatus>(raw));
        builder.Property(i => i.ExpiresAt).IsRequired();
        builder.Property(i => i.CompletedAt);
        builder.Property(i => i.CreatedAt).IsRequired();

        builder.HasOne(i => i.User)
            .WithMany()
            .HasForeignKey(i => i.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.Asset)
            .WithMany()
            .HasForeignKey(i => i.AssetId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.AssetVersion)
            .WithMany()
            .HasForeignKey(i => i.AssetVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.StripeSessionId)
            .IsUnique()
            .HasDatabaseName("UIX_checkout_intents_stripe_session");
        builder.HasIndex(i => new { i.AssetId, i.Status, i.ExpiresAt })
            .HasDatabaseName("IX_checkout_intents_asset_active");
        builder.HasIndex(i => new { i.UserId, i.AssetId })
            .IsUnique()
            .HasFilter("\"Status\" = 'PENDING'")
            .HasDatabaseName("UIX_checkout_intents_user_asset_pending");
    }
}
