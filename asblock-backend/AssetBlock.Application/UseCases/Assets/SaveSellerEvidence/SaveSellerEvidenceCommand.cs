using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Enums;
namespace AssetBlock.Application.UseCases.Assets.SaveSellerEvidence;

public sealed record SaveSellerEvidenceCommand(Guid OwnerId, Guid AssetId, Guid AssetVersionId, Guid WorkspaceId,
    Guid ReportId, Guid? FindingId, SellerResponseType? ResponseType, string Text,
    long ExpectedWorkspaceRevision, int ExpectedEvidenceRevision, Guid OperationId) : IRequest<Result<SellerEvidenceSavedDto>>;
