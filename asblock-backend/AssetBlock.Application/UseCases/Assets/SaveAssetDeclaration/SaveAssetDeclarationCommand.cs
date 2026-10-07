using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.SaveAssetDeclaration;

/// <summary>Structured source declaration save for the pre-upload or a version-scoped workspace.</summary>
public sealed record SaveAssetDeclarationCommand(
    Guid AssetId,
    Guid? AssetVersionId,
    Guid OwnerId,
    Guid OperationId,
    long ExpectedWorkspaceRevision,
    SourceDeclarationPayload Declaration) : IRequest<Result<DraftSaveResult>>;
