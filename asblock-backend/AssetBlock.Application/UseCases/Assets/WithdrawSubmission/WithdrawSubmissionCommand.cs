using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Moderation;

namespace AssetBlock.Application.UseCases.Assets.WithdrawSubmission;

public sealed record WithdrawSubmissionCommand(
    Guid OwnerId,
    Guid SubmissionId,
    long ExpectedCaseRevision,
    Guid OperationId) : IRequest<Result<WithdrawSubmissionResult>>;
