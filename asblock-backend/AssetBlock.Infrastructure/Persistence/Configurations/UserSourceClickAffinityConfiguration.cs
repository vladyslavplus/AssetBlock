using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class UserSourceClickAffinityConfiguration : IEntityTypeConfiguration<UserSourceClickAffinity>
{
    public void Configure(EntityTypeBuilder<UserSourceClickAffinity> builder)
    {
        builder.ToTable("user_source_click_affinity", table =>
        {
            table.HasCheckConstraint(
                "CK_user_source_click_affinity_clicks_non_negative",
                """
                "Clicks" >= 0
                """);
        });

        builder.HasKey(e => new { e.UserId, e.SourceAssetId, e.TargetAssetId });

        builder.Property(e => e.Clicks).IsRequired();
        builder.Property(e => e.LastClickedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
