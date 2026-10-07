using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.SaveAssetDraft;

/// <summary>Owner-only metadata draft write. Price is intentionally absent: it uses the dedicated price operation.</summary>
public sealed record SaveAssetDraftCommand(
    Guid AssetId,
    Guid OwnerId,
    Guid OperationId,
    long ExpectedWorkspaceRevision,
    SellerDraftMaterialPayload Material) : IRequest<Result<DraftSaveResult>>;
