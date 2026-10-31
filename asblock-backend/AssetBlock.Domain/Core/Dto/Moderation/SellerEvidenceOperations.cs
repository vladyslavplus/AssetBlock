using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Enums;
namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record SellerEvidenceSnapshot(Guid ReportId, IReadOnlyList<Guid> ResponseIds, string Text);
public sealed record SellerEvidenceWriteRequest(Guid ActorUserId, Guid AssetId, Guid AssetVersionId, Guid WorkspaceId,
    Guid ReportId, Guid? FindingId, SellerResponseType? ResponseType, string Text,
    long ExpectedWorkspaceRevision, int ExpectedEvidenceRevision, Guid OperationId, string RequestDigest);
public enum SellerEvidenceWriteStatus { SAVED, NOT_FOUND, FORBIDDEN, OPERATION_CONFLICT, STALE_WORKSPACE, STALE_EVIDENCE, INVALID_FINDING, ANALYSIS_UNAVAILABLE, INVALID_INPUT }
public sealed record SellerEvidenceWriteResult(SellerEvidenceWriteStatus Status, SellerEvidenceSavedDto? Saved = null);
public sealed record FindingDispositionInput(Guid FindingId, FindingDispositionType Disposition, string Rationale);
