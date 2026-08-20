using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CheckoutIntentItemConfiguration : IEntityTypeConfiguration<CheckoutIntentItem>
{
    public void Configure(EntityTypeBuilder<CheckoutIntentItem> builder)
    {
        builder.ToTable("checkout_intent_items", table =>
        {
            table.HasCheckConstraint("CK_checkout_intent_items_position_positive", "\"Position\" > 0");
            table.HasCheckConstraint("CK_checkout_intent_items_version_positive", "\"VersionNumber\" > 0");
            table.HasCheckConstraint("CK_checkout_intent_items_list_price_positive", "\"ListPrice\" > 0");
            table.HasCheckConstraint("CK_checkout_intent_items_allocated_price_positive", "\"AllocatedPrice\" > 0");
        });

        builder.HasKey(i => i.Id);
        builder.Property(i => i.CheckoutIntentId).IsRequired();
        builder.Property(i => i.AssetId).IsRequired();
        builder.Property(i => i.AssetVersionId).IsRequired();
        builder.Property(i => i.SellerId).IsRequired();
        builder.Property(i => i.Position).IsRequired();
        builder.Property(i => i.AssetTitleSnapshot).IsRequired().HasMaxLength(500);
        builder.Property(i => i.VersionNumber).IsRequired();
        builder.Property(i => i.ListPrice).IsRequired().HasPrecision(18, 2);
        builder.Property(i => i.AllocatedPrice).IsRequired().HasPrecision(18, 2);
        builder.Property(i => i.LicenseCode)
            .IsRequired()
            .HasMaxLength(64)
            .HasConversion(
                code => code.ToString(),
                raw => Enum.Parse<AssetLicenseCode>(raw));
        builder.Property(i => i.LicenseTemplateVersion).IsRequired().HasMaxLength(32);
        builder.Property(i => i.LicenseDisplayName).IsRequired().HasMaxLength(128);
        builder.Property(i => i.LicenseTerms).IsRequired().HasMaxLength(16000);

        foreach (var property in new[]
                 {
                     nameof(CheckoutIntentItem.CheckoutIntentId),
                     nameof(CheckoutIntentItem.AssetId),
                     nameof(CheckoutIntentItem.AssetVersionId),
                     nameof(CheckoutIntentItem.SellerId),
                     nameof(CheckoutIntentItem.Position),
                     nameof(CheckoutIntentItem.AssetTitleSnapshot),
                     nameof(CheckoutIntentItem.VersionNumber),
                     nameof(CheckoutIntentItem.ListPrice),
                     nameof(CheckoutIntentItem.AllocatedPrice),
                     nameof(CheckoutIntentItem.LicenseCode),
                     nameof(CheckoutIntentItem.LicenseTemplateVersion),
                     nameof(CheckoutIntentItem.LicenseDisplayName),
                     nameof(CheckoutIntentItem.LicenseTerms)
                 })
        {
            builder.Property(property).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }

        builder.HasOne(i => i.CheckoutIntent)
            .WithMany(c => c.Items)
            .HasForeignKey(i => i.CheckoutIntentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.Asset)
            .WithMany()
            .HasForeignKey(i => i.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.AssetVersion)
            .WithMany()
            .HasForeignKey(i => new { i.AssetId, i.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.Seller)
            .WithMany()
            .HasForeignKey(i => i.SellerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.CheckoutIntentId, i.Position })
            .IsUnique()
            .HasDatabaseName("UIX_checkout_intent_items_intent_position");

        builder.HasIndex(i => new { i.CheckoutIntentId, i.AssetId })
            .IsUnique()
            .HasDatabaseName("UIX_checkout_intent_items_intent_asset");

        builder.HasIndex(i => i.AssetId)
            .HasDatabaseName("IX_checkout_intent_items_asset");

        builder.HasIndex(i => new { i.SellerId, i.CheckoutIntentId })
            .HasDatabaseName("IX_checkout_intent_items_seller_intent");
    }
}
