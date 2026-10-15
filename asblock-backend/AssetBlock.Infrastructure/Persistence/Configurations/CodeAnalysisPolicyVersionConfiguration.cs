using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CodeAnalysisPolicyVersionConfiguration : IEntityTypeConfiguration<CodeAnalysisPolicyVersion>
{
    public void Configure(EntityTypeBuilder<CodeAnalysisPolicyVersion> builder)
    {
        builder.ToTable("code_analysis_policy_versions", table =>
        {
            table.HasCheckConstraint("CK_code_analysis_policy_versions_digest", "\"PolicyDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_policy_versions_stage_matrix", "jsonb_typeof(\"StageMatrixJson\") = 'object'");
            table.HasCheckConstraint("CK_code_analysis_policy_versions_tool_identities", "jsonb_typeof(\"ToolIdentitiesJson\") = 'object'");
            table.HasCheckConstraint("CK_code_analysis_policy_versions_limits", "jsonb_typeof(\"LimitsJson\") = 'object'");
            table.HasCheckConstraint("CK_code_analysis_policy_versions_security_validity", "jsonb_typeof(\"SecurityValidityJson\") = 'object'");
        });

        builder.HasKey(p => p.Id);

        builder.Property(p => p.PolicyKey).IsRequired().HasMaxLength(64);
        builder.Property(p => p.PolicyDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(p => p.SchemaName).IsRequired().HasMaxLength(64);
        builder.Property(p => p.StageMatrixJson).IsRequired().HasColumnType("jsonb");
        builder.Property(p => p.VerifierMode).IsRequired().HasMaxLength(48);
        builder.Property(p => p.VerifierReason).IsRequired().HasMaxLength(512);
        builder.Property(p => p.ToolIdentitiesJson).IsRequired().HasColumnType("jsonb");
        builder.Property(p => p.LimitsJson).IsRequired().HasColumnType("jsonb");
        builder.Property(p => p.SecurityValidityJson).IsRequired().HasColumnType("jsonb");
        builder.Property(p => p.ModelKey).HasColumnType("char(64)").IsFixedLength();
        builder.Property(p => p.IndexKey).HasColumnType("char(64)").IsFixedLength();
        builder.Property(p => p.ReferenceSnapshotId).HasMaxLength(128);
        builder.Property(p => p.CreatedAt).IsRequired();

        // Pinned policies are content addressed; a digest may never be edited under the same key.
        builder.HasIndex(p => new { p.PolicyKey, p.PolicyDigest })
            .IsUnique()
            .HasDatabaseName("UIX_code_analysis_policy_versions_identity");
    }
}
