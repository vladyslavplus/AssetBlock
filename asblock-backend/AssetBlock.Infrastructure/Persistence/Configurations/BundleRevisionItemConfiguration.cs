using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class BundleRevisionItemConfiguration : IEntityTypeConfiguration<BundleRevisionItem>
{
    public void Configure(EntityTypeBuilder<BundleRevisionItem> builder)
    {
        builder.ToTable("bundle_revision_items", table =>
        {
            table.HasCheckConstraint("CK_bundle_revision_items_position_positive", "\"Position\" > 0");
            table.HasCheckConstraint("CK_bundle_revision_items_list_price_positive", "\"ListPriceSnapshot\" > 0");
        });

        builder.HasKey(i => i.Id);
        builder.Property(i => i.BundleRevisionId).IsRequired();
        builder.Property(i => i.AssetId);
        builder.Property(i => i.Position).IsRequired();
        builder.Property(i => i.AssetTitleSnapshot).IsRequired().HasMaxLength(500);
        builder.Property(i => i.ListPriceSnapshot).IsRequired().HasPrecision(18, 2);

        builder.Property(i => i.BundleRevisionId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(i => i.Position).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(i => i.AssetTitleSnapshot).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(i => i.ListPriceSnapshot).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        // AssetId may become null on hard delete (SET NULL); do not throw after save.

        builder.HasOne(i => i.BundleRevision)
            .WithMany(r => r.Items)
            .HasForeignKey(i => i.BundleRevisionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.Asset)
            .WithMany()
            .HasForeignKey(i => i.AssetId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(i => new { i.BundleRevisionId, i.Position })
            .IsUnique()
            .HasDatabaseName("UIX_bundle_revision_items_revision_position");

        builder.HasIndex(i => new { i.BundleRevisionId, i.AssetId })
            .IsUnique()
            .HasFilter("\"AssetId\" IS NOT NULL")
            .HasDatabaseName("UIX_bundle_revision_items_revision_asset");
    }
}
