using AssetBlock.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.AuthorId).IsRequired();
        builder.Property(a => a.CategoryId).IsRequired();
        builder.Property(a => a.Title).IsRequired().HasMaxLength(500);
        builder.Property(a => a.Description).HasMaxLength(5000);
        builder.Property(a => a.Price).HasPrecision(18, 2);
        builder.Property(a => a.StorageKey).IsRequired().HasMaxLength(1024);
        builder.Property(a => a.FileName).IsRequired().HasMaxLength(512);
        builder.Property(a => a.EncryptionNonceBase64).IsRequired().HasMaxLength(64);
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt).IsRequired();

        builder.HasOne(a => a.Author)
            .WithMany(u => u.AuthoredAssets)
            .HasForeignKey(a => a.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Category)
            .WithMany(c => c.Assets)
            .HasForeignKey(a => a.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
