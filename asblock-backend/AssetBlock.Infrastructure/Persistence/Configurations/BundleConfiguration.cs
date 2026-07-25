using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class BundleConfiguration : IEntityTypeConfiguration<Bundle>
{
    public void Configure(EntityTypeBuilder<Bundle> builder)
    {
        builder.ToTable("bundles");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.SellerId).IsRequired();
        builder.Property(b => b.ArchivedAt);
        builder.Property(b => b.CreatedAt).IsRequired();
        builder.Property(b => b.UpdatedAt);

        builder.HasOne(b => b.Seller)
            .WithMany()
            .HasForeignKey(b => b.SellerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(b => new { b.SellerId, b.CreatedAt, b.Id })
            .HasDatabaseName("IX_bundles_seller_created");
        builder.HasIndex(b => new { b.ArchivedAt, b.CreatedAt, b.Id })
            .HasDatabaseName("IX_bundles_archived_created");
    }
}
