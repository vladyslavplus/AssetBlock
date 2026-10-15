using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CodeAnalysisReuseArtifactConfiguration : IEntityTypeConfiguration<CodeAnalysisReuseArtifact>
{
    public void Configure(EntityTypeBuilder<CodeAnalysisReuseArtifact> builder)
    {
        builder.ToTable("code_analysis_reuse_artifacts", table =>
        {
            table.HasCheckConstraint("CK_code_analysis_reuse_artifacts_stage", "\"Stage\" ~ '^[A-Z][A-Z0-9_]{0,31}$'");
            table.HasCheckConstraint("CK_code_analysis_reuse_artifacts_digest", "\"ArtifactDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_reuse_artifacts_payload", "jsonb_typeof(\"ArtifactJson\") = 'object'");
            // Reuse evidence stays bounded; raw source never enters the product database.
            table.HasCheckConstraint("CK_code_analysis_reuse_artifacts_size", "\"PayloadBytes\" > 0 AND octet_length(CAST(\"ArtifactJson\" AS text)) <= 262144");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Stage).IsRequired().HasMaxLength(32);
        builder.Property(a => a.ArtifactDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(a => a.ArtifactJson).IsRequired().HasColumnType("jsonb");
        builder.Property(a => a.PayloadBytes).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();

        builder.HasOne<CodeAnalysisReportHeader>().WithMany().HasForeignKey(a => a.SourceReportId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CodeAnalysisRun>().WithMany().HasForeignKey(a => a.SourceRunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CodeAnalysisReportStage>().WithMany().HasForeignKey(a => a.SourceStageId).OnDelete(DeleteBehavior.Restrict);

        // Reuse is only possible from an exact verified artifact of one stage of one run.
        builder.HasIndex(a => new { a.SourceRunId, a.Stage })
            .IsUnique()
            .HasDatabaseName("UIX_code_analysis_reuse_artifacts_run_stage");

        builder.HasIndex(a => new { a.Stage, a.ArtifactDigest })
            .HasDatabaseName("IX_code_analysis_reuse_artifacts_digest");
    }
}
