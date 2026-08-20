using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class BundleRevisionConfiguration : IEntityTypeConfiguration<BundleRevision>
{
    public void Configure(EntityTypeBuilder<BundleRevision> builder)
    {
        builder.ToTable("bundle_revisions", table =>
        {
            table.HasCheckConstraint("CK_bundle_revisions_revision_number_positive", "\"RevisionNumber\" > 0");
            table.HasCheckConstraint("CK_bundle_revisions_price_positive", "\"Price\" > 0");
            table.HasCheckConstraint("CK_bundle_revisions_list_price_total_positive", "\"ListPriceTotal\" > 0");
            table.HasCheckConstraint(
                "CK_bundle_revisions_price_below_list_total",
                "\"Price\" < \"ListPriceTotal\"");
            table.HasCheckConstraint(
                "CK_bundle_revisions_currency_iso_lower",
                "length(\"Currency\") = 3 AND \"Currency\" = lower(\"Currency\")");
            table.HasCheckConstraint(
                "CK_bundle_revisions_currency_usd_v1",
                "\"Currency\" = 'usd'");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.BundleId).IsRequired();
        builder.Property(r => r.RevisionNumber).IsRequired();
        builder.Property(r => r.IsCurrent).IsRequired();
        builder.Property(r => r.Title).IsRequired().HasMaxLength(160);
        builder.Property(r => r.Description).HasMaxLength(2000);
        builder.Property(r => r.Price).IsRequired().HasPrecision(18, 2);
        builder.Property(r => r.Currency).IsRequired().HasMaxLength(3);
        builder.Property(r => r.ListPriceTotal).IsRequired().HasPrecision(18, 2);
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Ignore(r => r.UpdatedAt);

        // Snapshot fields are immutable after insert; only IsCurrent flips.
        builder.Property(r => r.BundleId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(r => r.RevisionNumber).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(r => r.Title).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(r => r.Description).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(r => r.Price).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(r => r.Currency).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(r => r.ListPriceTotal).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        builder.HasOne(r => r.Bundle)
            .WithMany(b => b.Revisions)
            .HasForeignKey(r => r.BundleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.BundleId, r.RevisionNumber })
            .IsUnique()
            .HasDatabaseName("UIX_bundle_revisions_bundle_number");

        builder.HasIndex(r => r.BundleId)
            .IsUnique()
            .HasFilter("\"IsCurrent\" = true")
            .HasDatabaseName("UIX_bundle_revisions_bundle_current");
    }
}
