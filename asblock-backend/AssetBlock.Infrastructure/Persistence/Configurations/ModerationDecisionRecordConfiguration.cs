using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class ModerationDecisionRecordConfiguration : IEntityTypeConfiguration<ModerationDecisionRecord>
{
    public void Configure(EntityTypeBuilder<ModerationDecisionRecord> builder)
    {
        builder.ToTable("moderation_decisions");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Outcome).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(d => d.Message).IsRequired().HasMaxLength(2000);

        builder.HasOne(d => d.Submission).WithMany().HasForeignKey(d => d.SubmissionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(d => d.PublicationSnapshot).WithMany().HasForeignKey(d => d.PublicationSnapshotId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => d.SubmissionId).IsUnique();
    }
}
