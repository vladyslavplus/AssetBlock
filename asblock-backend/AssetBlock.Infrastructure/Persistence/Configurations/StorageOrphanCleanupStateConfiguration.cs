using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class StorageOrphanCleanupStateConfiguration : IEntityTypeConfiguration<StorageOrphanCleanupState>
{
    public void Configure(EntityTypeBuilder<StorageOrphanCleanupState> builder)
    {
        builder.ToTable("storage_orphan_cleanup_states", table => table.HasCheckConstraint(
            "CK_storage_orphan_cleanup_states_SingletonId",
            "\"Id\" = 1"));
        builder.HasKey(state => state.Id);
        builder.Property(state => state.Id).ValueGeneratedNever();
        builder.Property(state => state.CursorKey).HasMaxLength(1024);
        builder.Property(state => state.CursorVersionId).HasMaxLength(1024);
        builder.Property(state => state.LeaseId);
        builder.Property(state => state.LeaseUntil);
    }
}
