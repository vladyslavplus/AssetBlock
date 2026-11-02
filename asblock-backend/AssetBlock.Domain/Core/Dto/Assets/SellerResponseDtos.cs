using AssetBlock.Domain.Core.Enums;
namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record SellerResponseRequest(Guid WorkspaceId, Guid ReportId, Guid FindingId,
    long ExpectedWorkspaceRevision, int ExpectedEvidenceRevision, Guid OperationId, SellerResponseType ResponseType, string Text);
public sealed record SellerEvidenceEditRequest(Guid WorkspaceId, Guid ReportId,
    long ExpectedWorkspaceRevision, int ExpectedEvidenceRevision, Guid OperationId, string Text);
public sealed record SellerEvidenceSavedDto(Guid? ResponseId, Guid WorkspaceId, long WorkspaceRevision,
    int EvidenceRevision, long CaseRevision, bool Replayed = false);
public sealed record SellerResponseDto(Guid Id, Guid AssetVersionId, Guid ReportId, Guid FindingId,
    int EvidenceRevision, SellerResponseType ResponseType, string Text, DateTimeOffset CreatedAt, Guid ActorUserId, bool IsCurrent);
public sealed record SellerResponsesPageDto(Guid WorkspaceId, long WorkspaceRevision, long CaseRevision,
    int EvidenceRevision, Guid? ReportId, int RequiredCount, int OutstandingCount, string EvidenceText,
    IReadOnlyList<SellerResponseDto> Items, int TotalCount, int Page, int PageSize, int TotalPages);
