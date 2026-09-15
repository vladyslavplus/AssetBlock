using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class UserTagAffinityConfiguration : IEntityTypeConfiguration<UserTagAffinity>
{
    public void Configure(EntityTypeBuilder<UserTagAffinity> builder)
    {
        builder.ToTable("user_tag_affinity", table =>
        {
            table.HasCheckConstraint(
                "CK_user_tag_affinity_counters_non_negative",
                """
                "Purchases" >= 0 AND "Reviews" >= 0
                """);
        });

        builder.HasKey(e => new { e.UserId, e.TagId });

        builder.Property(e => e.Purchases).IsRequired();
        builder.Property(e => e.Reviews).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
