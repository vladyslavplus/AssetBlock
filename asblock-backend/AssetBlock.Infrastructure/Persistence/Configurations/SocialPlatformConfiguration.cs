using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class SocialPlatformConfiguration : IEntityTypeConfiguration<SocialPlatform>
{
    public void Configure(EntityTypeBuilder<SocialPlatform> builder)
    {
        builder.ToTable("social_platforms");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(50);
        builder.Property(x => x.IconName).IsRequired().HasMaxLength(50);
        builder.HasIndex(x => x.Name).IsUnique();

        var seedCreatedAt = new DateTimeOffset(2026, 2, 23, 0, 0, 0, TimeSpan.Zero);
        builder.HasData(
            new SocialPlatform { Id = Guid.Parse("a7b3c4d5-e6f1-4a2b-9c8d-7e6f5a4b3c2d"), Name = "Twitter / X", IconName = "twitter", CreatedAt = seedCreatedAt },
            new SocialPlatform { Id = Guid.Parse("b8c4d5e6-f7a2-5b3c-0d9e-8f7a6b5c4d3e"), Name = "GitHub", IconName = "github", CreatedAt = seedCreatedAt },
            new SocialPlatform { Id = Guid.Parse("c9d5e6f7-a8b3-6c4d-1e0f-9a8b7c6d5e4f"), Name = "LinkedIn", IconName = "linkedin", CreatedAt = seedCreatedAt },
            new SocialPlatform { Id = Guid.Parse("d0e6f7a8-b9c4-7d5e-2f1a-0b9c8d7e6f5a"), Name = "YouTube", IconName = "youtube", CreatedAt = seedCreatedAt },
            new SocialPlatform { Id = Guid.Parse("e1f7a8b9-c0d5-8e6f-3a2b-1c0d9e8f7a6b"), Name = "Discord", IconName = "discord", CreatedAt = seedCreatedAt },
            new SocialPlatform { Id = Guid.Parse("f2a8b9c0-d1e6-9f7a-4b3c-2d1e0f9a8b7c"), Name = "Instagram", IconName = "instagram", CreatedAt = seedCreatedAt },
            new SocialPlatform { Id = Guid.Parse("a3b9c0d1-e2f7-0a8b-5c4d-3e2f1a0b9c8d"), Name = "ArtStation", IconName = "artstation", CreatedAt = seedCreatedAt },
            new SocialPlatform { Id = Guid.Parse("b4c0d1e2-f3a8-1b9c-6d5e-4f3a2b1c0d9e"), Name = "Personal Website", IconName = "globe", CreatedAt = seedCreatedAt }
        );
    }
}
