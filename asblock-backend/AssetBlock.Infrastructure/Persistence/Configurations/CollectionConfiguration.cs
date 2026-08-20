using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CollectionConfiguration : IEntityTypeConfiguration<Collection>
{
    public void Configure(EntityTypeBuilder<Collection> builder)
    {
        builder.ToTable("collections");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.SellerId).IsRequired();
        builder.Property(c => c.Title).IsRequired().HasMaxLength(160);
        builder.Property(c => c.Description).HasMaxLength(2000);
        builder.Property(c => c.Status)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(
                status => status.ToString(),
                raw => Enum.Parse<CollectionStatus>(raw));
        builder.Property(c => c.PublishedAt);
        builder.Property(c => c.ArchivedAt);
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt);

        builder.HasOne(c => c.Seller)
            .WithMany()
            .HasForeignKey(c => c.SellerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.SellerId, c.Status, c.CreatedAt, c.Id })
            .HasDatabaseName("IX_collections_seller_status_created");
        builder.HasIndex(c => new { c.Status, c.PublishedAt, c.Id })
            .HasDatabaseName("IX_collections_public_status_published");
    }
}
