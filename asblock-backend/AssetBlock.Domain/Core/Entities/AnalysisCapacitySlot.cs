using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Durable shared capacity slot for an external analysis service. Claiming a job reserves a free
/// slot in the same short transaction, so ordinary waiting never consumes an attempt and a zombie
/// execution cannot overlap its replacement.
/// </summary>
public class AnalysisCapacitySlot : BaseEntity
{
    /// <summary>Stable service identity shared by every worker instance talking to one instance.</summary>
    public required string ServiceKey { get; set; }

    public required int SlotNumber { get; set; }

    /// <summary>Configured capacity for this service; a mismatch against configuration fails closed.</summary>
    public required int Capacity { get; set; }

    public AnalysisCapacitySlotState State { get; set; } = AnalysisCapacitySlotState.FREE;

    public Guid? ReservedRunId { get; set; }

    public Guid? ReservedJobId { get; set; }

    /// <summary>Transport lease token bound to the reservation, never the job lease Guid.</summary>
    public string? LeaseToken { get; set; }

    public int? ReservationGeneration { get; set; }

    /// <summary>Immutable execution deadline for the reserved execution; renewal never extends it.</summary>
    public DateTimeOffset? HardDeadlineUtc { get; set; }

    /// <summary>Bounded grace after the hard deadline before reassignment may be considered.</summary>
    public DateTimeOffset? CleanupGraceUntil { get; set; }

    /// <summary>Set only when the caller actually confirmed the execution ended, never fabricated.</summary>
    public DateTimeOffset? ConfirmedTerminationAt { get; set; }

    /// <summary>Why the reservation was released, so reconciliation never claims a false confirmation.</summary>
    public string? ReleasedReason { get; set; }

    public DateTimeOffset? ReservedAt { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }
}
