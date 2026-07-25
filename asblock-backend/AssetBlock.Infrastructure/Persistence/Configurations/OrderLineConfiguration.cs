using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("order_lines", table =>
        {
            table.HasCheckConstraint("CK_order_lines_position_positive", "\"Position\" > 0");
            table.HasCheckConstraint("CK_order_lines_version_positive", "\"VersionNumber\" > 0");
            table.HasCheckConstraint("CK_order_lines_list_price_positive", "\"ListPrice\" > 0");
            table.HasCheckConstraint("CK_order_lines_price_paid_positive", "\"PricePaid\" > 0");
        });

        builder.HasKey(l => l.Id);
        builder.Property(l => l.OrderId).IsRequired();
        builder.Property(l => l.AssetId).IsRequired();
        builder.Property(l => l.AssetVersionId).IsRequired();
        builder.Property(l => l.SellerId).IsRequired();
        builder.Property(l => l.Position).IsRequired();
        builder.Property(l => l.AssetTitleSnapshot).IsRequired().HasMaxLength(500);
        builder.Property(l => l.VersionNumber).IsRequired();
        builder.Property(l => l.ListPrice).IsRequired().HasPrecision(18, 2);
        builder.Property(l => l.PricePaid).IsRequired().HasPrecision(18, 2);
        builder.Property(l => l.LicenseCode)
            .IsRequired()
            .HasMaxLength(64)
            .HasConversion(
                code => code.ToString(),
                raw => Enum.Parse<AssetLicenseCode>(raw));
        builder.Property(l => l.LicenseTemplateVersion).IsRequired().HasMaxLength(32);
        builder.Property(l => l.LicenseDisplayName).IsRequired().HasMaxLength(128);
        builder.Property(l => l.LicenseTerms).IsRequired().HasMaxLength(16000);

        foreach (var property in new[]
                 {
                     nameof(OrderLine.OrderId),
                     nameof(OrderLine.AssetId),
                     nameof(OrderLine.AssetVersionId),
                     nameof(OrderLine.SellerId),
                     nameof(OrderLine.Position),
                     nameof(OrderLine.AssetTitleSnapshot),
                     nameof(OrderLine.VersionNumber),
                     nameof(OrderLine.ListPrice),
                     nameof(OrderLine.PricePaid),
                     nameof(OrderLine.LicenseCode),
                     nameof(OrderLine.LicenseTemplateVersion),
                     nameof(OrderLine.LicenseDisplayName),
                     nameof(OrderLine.LicenseTerms)
                 })
        {
            builder.Property(property).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }

        builder.HasOne(l => l.Order)
            .WithMany(o => o.Lines)
            .HasForeignKey(l => l.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.Asset)
            .WithMany()
            .HasForeignKey(l => l.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.AssetVersion)
            .WithMany()
            .HasForeignKey(l => l.AssetVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.Seller)
            .WithMany()
            .HasForeignKey(l => l.SellerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.OrderId, l.Position })
            .IsUnique()
            .HasDatabaseName("UIX_order_lines_order_position");

        builder.HasIndex(l => new { l.OrderId, l.AssetId })
            .IsUnique()
            .HasDatabaseName("UIX_order_lines_order_asset");

        builder.HasIndex(l => new { l.SellerId, l.OrderId })
            .HasDatabaseName("IX_order_lines_seller_order");
    }
}
