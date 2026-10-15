using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Everything one admitted analysis execution needs, already proven against live state.</summary>
public sealed record AnalysisReservationBinding(
    string ServiceKey,
    int SlotNumber,
    int ReservationGeneration,
    string TransportLeaseToken,
    DateTimeOffset HardDeadlineUtc,
    string PolicyKey,
    string DeclarationDigest,
    string MaterialDigest,
    IReadOnlyList<CodeAnalysisDeclaredLicense> DeclaredLicenses,
    CodeAnalysisReuseRequest? Reuse = null);

/// <summary>
/// Resolves the live reservation bound to a claimed analysis job. Every value is re-proven against
/// current database state rather than trusted from the job payload, and any mismatch fails closed
/// instead of producing a partially trusted binding.
/// </summary>
public interface IAnalysisReservationReader
{
    /// <summary>
    /// Returns the binding for the exact claimed job, or null when the run, the slot, the pinned
    /// policy, or the declaration no longer agree with the claim.
    /// </summary>
    Task<AnalysisReservationBinding?> Read(
        AssetProcessingJobContext<CodeAnalysisPayload> context,
        CancellationToken cancellationToken = default);
}
