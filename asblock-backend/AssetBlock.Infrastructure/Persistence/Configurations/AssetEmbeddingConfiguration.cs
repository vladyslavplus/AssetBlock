using AssetBlock.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AssetEmbeddingConfiguration : IEntityTypeConfiguration<AssetEmbedding>
{
    private const int EMBEDDING_DIMENSION = 768;

    public void Configure(EntityTypeBuilder<AssetEmbedding> builder)
    {
        builder.ToTable("asset_embeddings", table =>
        {
            table.HasCheckConstraint("CK_asset_embeddings_model_key", "\"ModelKey\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_asset_embeddings_content_hash", "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_asset_embeddings_model_digest", "\"ModelDigest\" ~ '^sha256:[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_asset_embeddings_dimension", $"\"Dimension\" = {EMBEDDING_DIMENSION}");
            table.HasCheckConstraint("CK_asset_embeddings_source_revision", "\"SourceRevision\" > 0");
            table.HasCheckConstraint("CK_asset_embeddings_vector_dims", "vector_dims(\"Embedding\") = \"Dimension\"");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.AssetId).IsRequired();

        builder.Property(e => e.ModelKey)
            .IsRequired()
            .HasColumnType("char(64)")
            .IsFixedLength();

        builder.Property(e => e.Provider)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(e => e.ModelId)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.ModelRevision)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.ModelDigest)
            .IsRequired()
            .HasMaxLength(71);

        builder.Property(e => e.Dimension)
            .IsRequired();

        builder.Property(e => e.ContentSchemaVersion)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(e => e.SourceRevision)
            .IsRequired();

        builder.Property(e => e.ContentHash)
            .IsRequired()
            .HasColumnType("char(64)")
            .IsFixedLength();

        builder.Property(e => e.Embedding)
            .IsRequired()
            .HasColumnType($"vector({EMBEDDING_DIMENSION})");

        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        builder.HasOne(e => e.Asset)
            .WithMany()
            .HasForeignKey(e => e.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.AssetId, e.ModelKey })
            .IsUnique()
            .HasDatabaseName("UIX_asset_embeddings_asset_id_model_key");

        builder.HasIndex(e => new { e.ModelKey, e.AssetId })
            .HasDatabaseName("IX_asset_embeddings_model_key_asset_id");
    }
}
