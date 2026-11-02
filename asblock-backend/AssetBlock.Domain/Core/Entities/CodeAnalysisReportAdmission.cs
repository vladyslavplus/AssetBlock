using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Server-issued review eligibility for one validated report. This is the trusted admission record;
/// a report header alone, or any admission field supplied by the analysis service, proves nothing.
/// </summary>
public class CodeAnalysisReportAdmission : BaseEntity
{
    public required Guid ReportId { get; set; }

    public required CodeAnalysisAdmissionKind AdmissionKind { get; set; }

    public required string AdmittedBy { get; set; }

    public required DateTimeOffset AdmittedAt { get; set; }

    public required string ReportDigest { get; set; }

    /// <summary>SHA-256 report identity produced by the analysis service over the pre-REPORT body.</summary>
    public required string PythonReportId { get; set; }

    public required string InputDigest { get; set; }

    public required string ServiceKey { get; set; }

    public required int ReservationSlotNumber { get; set; }

    public required int ReservationGeneration { get; set; }

    /// <summary>Digest of the transport lease token, never the token itself.</summary>
    public required string ReservationLeaseTokenDigest { get; set; }

    /// <summary>Server eligibility for human review; publication still requires an explicit decision.</summary>
    public required bool ReviewEligible { get; set; }

    public required string ReviewEligibilityReason { get; set; }
}
