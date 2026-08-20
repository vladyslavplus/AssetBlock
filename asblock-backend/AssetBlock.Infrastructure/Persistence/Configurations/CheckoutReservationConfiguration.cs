using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CheckoutReservationConfiguration : IEntityTypeConfiguration<CheckoutReservation>
{
    public const string UNIQUE_USER_ASSET = "UIX_checkout_reservations_user_asset";
    private const string UNIQUE_INTENT_ASSET = "UIX_checkout_reservations_intent_asset";

    public void Configure(EntityTypeBuilder<CheckoutReservation> builder)
    {
        builder.ToTable("checkout_reservations", table =>
        {
            table.HasCheckConstraint(
                "CK_checkout_reservations_expires_after_created",
                "\"ExpiresAt\" > \"CreatedAt\"");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.CheckoutIntentId).IsRequired();
        builder.Property(r => r.UserId).IsRequired();
        builder.Property(r => r.AssetId).IsRequired();
        builder.Property(r => r.ExpiresAt).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();

        builder.HasOne(r => r.CheckoutIntent)
            .WithMany(i => i.Reservations)
            .HasForeignKey(r => r.CheckoutIntentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Asset)
            .WithMany()
            .HasForeignKey(r => r.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.UserId, r.AssetId })
            .IsUnique()
            .HasDatabaseName(UNIQUE_USER_ASSET);

        builder.HasIndex(r => new { r.CheckoutIntentId, r.AssetId })
            .IsUnique()
            .HasDatabaseName(UNIQUE_INTENT_ASSET);

        builder.HasIndex(r => new { r.ExpiresAt, r.Id })
            .HasDatabaseName("IX_checkout_reservations_expires");
    }
}
