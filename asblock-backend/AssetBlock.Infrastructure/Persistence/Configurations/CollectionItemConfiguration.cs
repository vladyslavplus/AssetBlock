using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CollectionItemConfiguration : IEntityTypeConfiguration<CollectionItem>
{
    public void Configure(EntityTypeBuilder<CollectionItem> builder)
    {
        builder.ToTable("collection_items", table =>
        {
            table.HasCheckConstraint("CK_collection_items_position_positive", "\"Position\" > 0");
        });

        builder.HasKey(i => new { i.CollectionId, i.AssetId });
        builder.Property(i => i.Position).IsRequired();
        builder.Property(i => i.CreatedAt).IsRequired();

        builder.HasOne(i => i.Collection)
            .WithMany(c => c.Items)
            .HasForeignKey(i => i.CollectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.Asset)
            .WithMany()
            .HasForeignKey(i => i.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => new { i.CollectionId, i.Position })
            .IsUnique()
            .HasDatabaseName("UIX_collection_items_collection_position");
    }
}
