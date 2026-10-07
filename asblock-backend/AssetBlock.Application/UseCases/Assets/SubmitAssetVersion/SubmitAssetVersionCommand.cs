using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Assets.SubmitAssetVersion;

public sealed record SubmitAssetVersionCommand(
    Guid OwnerId,
    Guid AssetId,
    Guid AssetVersionId,
    Guid WorkspaceId,
    long ExpectedWorkspaceRevision,
    long ExpectedCaseRevision,
    Guid OperationId) : IRequest<Result<Guid>>;
