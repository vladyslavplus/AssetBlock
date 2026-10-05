using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class JsonMutationIdempotencyRecordConfiguration : IEntityTypeConfiguration<JsonMutationIdempotencyRecord>
{
    public const string UNIQUE_OPERATION_INDEX = "IX_json_mutation_idempotency_ActorUserId_OperationKind_OperationId";

    public void Configure(EntityTypeBuilder<JsonMutationIdempotencyRecord> builder)
    {
        builder.ToTable("json_mutation_idempotency");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.OperationKind).IsRequired().HasMaxLength(64);
        builder.Property(r => r.RequestDigest).IsRequired().HasColumnType("char(64)");
        builder.Property(r => r.ResultJson).HasColumnType("jsonb").IsRequired();

        builder.HasOne(r => r.ActorUser).WithMany().HasForeignKey(r => r.ActorUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.ActorUserId, r.OperationKind, r.OperationId })
            .IsUnique()
            .HasDatabaseName(UNIQUE_OPERATION_INDEX);
        builder.HasIndex(r => new { r.ActorUserId, r.OperationKind, r.RequestDigest });
    }
}
