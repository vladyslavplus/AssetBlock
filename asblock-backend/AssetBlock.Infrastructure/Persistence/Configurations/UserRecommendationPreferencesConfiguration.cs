using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class UserRecommendationPreferencesConfiguration : IEntityTypeConfiguration<UserRecommendationPreferences>
{
    public void Configure(EntityTypeBuilder<UserRecommendationPreferences> builder)
    {
        builder.ToTable("user_recommendation_preferences");

        builder.HasKey(e => e.UserId);

        builder.Property(e => e.IsPersonalized).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.OptedInAt);
        builder.Property(e => e.UpdatedAt).IsRequired();

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
