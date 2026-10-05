using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class ModerationSubmissionHistoryEntryConfiguration : IEntityTypeConfiguration<ModerationSubmissionHistoryEntry>
{
    public void Configure(EntityTypeBuilder<ModerationSubmissionHistoryEntry> builder)
    {
        builder.ToTable("moderation_submission_history");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(e => e.WithdrawalReason).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Summary).IsRequired().HasMaxLength(500);

        builder.HasOne(e => e.Submission).WithMany().HasForeignKey(e => e.SubmissionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.SubmissionId, e.CreatedAt, e.Id });
    }
}
