using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AnalysisCapacitySlotConfiguration : IEntityTypeConfiguration<AnalysisCapacitySlot>
{
    public void Configure(EntityTypeBuilder<AnalysisCapacitySlot> builder)
    {
        builder.ToTable("analysis_capacity_slots", table =>
        {
            table.HasCheckConstraint("CK_analysis_capacity_slots_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
            table.HasCheckConstraint("CK_analysis_capacity_slots_slot_number", "\"SlotNumber\" >= 0");
            table.HasCheckConstraint("CK_analysis_capacity_slots_capacity", "\"Capacity\" > 0");
            table.HasCheckConstraint(
                "CK_analysis_capacity_slots_state",
                "\"State\" IN ('FREE', 'RESERVED', 'RELEASING', 'RECOVERING')");
            table.HasCheckConstraint(
                "CK_analysis_capacity_slots_lease_token",
                "\"LeaseToken\" IS NULL OR \"LeaseToken\" ~ '^[0-9a-f]{64}$'");
            // A slot is free only when it carries no reservation identity at all.
            table.HasCheckConstraint(
                "CK_analysis_capacity_slots_reservation",
                "(\"State\" = 'FREE' AND \"ReservedRunId\" IS NULL AND \"ReservedJobId\" IS NULL AND \"LeaseToken\" IS NULL AND \"ReservationGeneration\" IS NULL AND \"HardDeadlineUtc\" IS NULL) OR " +
                "(\"State\" <> 'FREE' AND \"ReservedRunId\" IS NOT NULL AND \"ReservedJobId\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"ReservationGeneration\" IS NOT NULL AND \"HardDeadlineUtc\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_analysis_capacity_slots_release_reason",
                "(\"State\" = 'FREE' AND \"ReleasedReason\" IS NOT NULL AND length(\"ReleasedReason\") > 0) OR \"State\" <> 'FREE'");
        });

        builder.HasKey(s => s.Id);

        builder.Property(s => s.ServiceKey).IsRequired().HasMaxLength(128);
        builder.Property(s => s.SlotNumber).IsRequired();
        builder.Property(s => s.Capacity).IsRequired();
        builder.Property(s => s.State)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(s => s.ToString(), s => Enum.Parse<AnalysisCapacitySlotState>(s));
        builder.Property(s => s.LeaseToken).HasColumnType("char(64)").IsFixedLength();
        builder.Property(s => s.HardDeadlineUtc);
        builder.Property(s => s.CleanupGraceUntil);
        builder.Property(s => s.ConfirmedTerminationAt);
        builder.Property(s => s.ReleasedReason).HasMaxLength(64);

        // A confirmation timestamp exists only when termination was actually confirmed.
        builder.HasIndex(s => s.ServiceKey).HasDatabaseName("IX_analysis_capacity_slots_service");
        builder.Property(s => s.ReservedAt);
        builder.Property(s => s.ReleasedAt);
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasOne<CodeAnalysisRun>()
            .WithMany()
            .HasForeignKey(s => s.ReservedRunId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AssetProcessingJob>()
            .WithMany()
            .HasForeignKey(s => s.ReservedJobId)
            .OnDelete(DeleteBehavior.Restrict);

        // One row per shared slot of the service, shared by every worker instance.
        builder.HasIndex(s => new { s.ServiceKey, s.SlotNumber })
            .IsUnique()
            .HasDatabaseName("UIX_analysis_capacity_slots_service_slot");

        builder.HasIndex(s => new { s.ServiceKey, s.State })
            .HasDatabaseName("IX_analysis_capacity_slots_service_state");

        builder.HasIndex(s => s.HardDeadlineUtc)
            .HasDatabaseName("IX_analysis_capacity_slots_hard_deadline");
    }
}
