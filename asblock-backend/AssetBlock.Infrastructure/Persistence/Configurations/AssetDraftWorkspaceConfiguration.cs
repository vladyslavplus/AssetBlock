using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AssetDraftWorkspaceConfiguration : IEntityTypeConfiguration<AssetDraftWorkspace>
{
    private const string UNIQUE_PRE_UPLOAD = "UIX_asset_draft_workspaces_asset_pre_upload";
    private const string UNIQUE_VERSION = "UIX_asset_draft_workspaces_asset_version";

    private static readonly string _preUploadScopeKeyLiteral =
        $"'{DraftWorkspaceVersionScopes.PreUpload:D}'::uuid";

    public void Configure(EntityTypeBuilder<AssetDraftWorkspace> builder)
    {
        builder.ToTable("asset_draft_workspaces", table =>
        {
            table.HasCheckConstraint("CK_asset_draft_workspaces_revision", "\"WorkspaceRevision\" > 0");
            table.HasCheckConstraint("CK_asset_draft_workspaces_case_revision", "\"CaseRevision\" > 0");
            table.HasCheckConstraint(
                "CK_asset_draft_workspaces_version_scope_key",
                $"""
                (("AssetVersionId" IS NULL AND "WorkspaceVersionScopeKey" = {_preUploadScopeKeyLiteral})
                OR ("AssetVersionId" IS NOT NULL AND "WorkspaceVersionScopeKey" = "AssetVersionId"))
                """);
        });

        builder.HasKey(w => w.Id);
        builder.Property(w => w.AssetId).IsRequired();
        builder.Property(w => w.AssetVersionId).IsRequired(false);
        builder.Property(w => w.WorkspaceVersionScopeKey).IsRequired();
        builder.Property(w => w.ScopeId).IsRequired();
        builder.Property(w => w.WorkspaceRevision).IsRequired();
        builder.Property(w => w.CaseRevision).IsRequired();

        builder.HasAlternateKey(w => new { w.AssetId, w.Id, w.WorkspaceVersionScopeKey });

        builder.HasOne(w => w.Asset)
            .WithMany()
            .HasForeignKey(w => w.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(w => w.AssetVersion)
            .WithMany()
            .HasForeignKey(w => new { w.AssetId, w.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(w => w.AssetId)
            .IsUnique()
            .HasFilter("\"AssetVersionId\" IS NULL")
            .HasDatabaseName(UNIQUE_PRE_UPLOAD);

        builder.HasIndex(w => new { w.AssetId, w.AssetVersionId })
            .IsUnique()
            .HasFilter("\"AssetVersionId\" IS NOT NULL")
            .HasDatabaseName(UNIQUE_VERSION);
    }
}
