using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs", table =>
        {
            table.HasCheckConstraint(
                "CK_audit_logs_ActorType",
                $"\"ActorType\" IN ('{nameof(AuditActorType.USER)}', '{nameof(AuditActorType.SYSTEM)}', '{nameof(AuditActorType.ANONYMOUS)}')");
            table.HasCheckConstraint(
                "CK_audit_logs_Outcome",
                $"\"Outcome\" IN ('{nameof(AuditOutcome.SUCCESS)}', '{nameof(AuditOutcome.FAILURE)}', '{nameof(AuditOutcome.DENIED)}')");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityByDefaultColumn();

        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.ActorType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.ActorUserId);
        builder.Property(e => e.Action).IsRequired().HasMaxLength(AuditFieldLimits.ACTION_MAX_LENGTH);
        builder.Property(e => e.Outcome)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.ResourceType).IsRequired().HasMaxLength(AuditFieldLimits.RESOURCE_TYPE_MAX_LENGTH);
        builder.Property(e => e.ResourceId).HasMaxLength(AuditFieldLimits.RESOURCE_ID_MAX_LENGTH);
        builder.Property(e => e.TraceId).HasMaxLength(AuditFieldLimits.TRACE_ID_MAX_LENGTH);
        builder.Property(e => e.IpAddress).HasMaxLength(AuditFieldLimits.IP_ADDRESS_MAX_LENGTH);
        builder.Property(e => e.UserAgent).HasMaxLength(AuditFieldLimits.USER_AGENT_MAX_LENGTH);
        builder.Property(e => e.MetadataJson).HasColumnType("jsonb");

        builder.HasIndex(e => new { e.OccurredAt, e.Id })
            .IsDescending(true, true)
            .HasDatabaseName("IX_audit_logs_OccurredAt_Id");

        builder.HasIndex(e => new { e.ActorUserId, e.OccurredAt, e.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_audit_logs_ActorUserId_OccurredAt_Id");

        builder.HasIndex(e => new { e.Action, e.OccurredAt, e.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_audit_logs_Action_OccurredAt_Id");

        builder.HasIndex(e => new { e.Outcome, e.OccurredAt, e.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_audit_logs_Outcome_OccurredAt_Id");

        builder.HasIndex(e => new { e.ResourceType, e.ResourceId, e.OccurredAt, e.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("IX_audit_logs_ResourceType_ResourceId_OccurredAt_Id");
    }
}
