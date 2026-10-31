using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Moderation.CaseAssignment;

public sealed record ChangeCaseClaimCommand(Guid ActorId, Guid SubmissionId, Guid OperationId, long ExpectedCaseRevision, bool Claim) : IRequest<Result<CaseClaimSaved>>;

internal sealed class ChangeCaseClaimCommandValidator : AbstractValidator<ChangeCaseClaimCommand>
{
    public ChangeCaseClaimCommandValidator()
    {
        RuleFor(x => x.ActorId).NotEmpty();
        RuleFor(x => x.SubmissionId).NotEmpty();
        RuleFor(x => x.OperationId).NotEmpty();
        RuleFor(x => x.ExpectedCaseRevision).GreaterThan(0);
    }
}

internal sealed class ChangeCaseClaimCommandHandler(IModerationFoundationStore store, IUserStore users) : IRequestHandler<ChangeCaseClaimCommand, Result<CaseClaimSaved>>
{
    public async Task<Result<CaseClaimSaved>> Handle(ChangeCaseClaimCommand command, CancellationToken ct)
    {
        if (!AppRoles.CanReview((await users.GetPersistedRole(command.ActorId, ct))?.Role))
        { return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN); }
        var digest = DraftPayloadJson.ComputeDigest(DraftPayloadJson.Serialize(new { command.SubmissionId, command.ExpectedCaseRevision, command.Claim }));
        CaseClaimResult result = await store.ChangeCaseClaim(new(command.ActorId, command.SubmissionId, command.OperationId, command.ExpectedCaseRevision, command.Claim, digest), ct);
        return result.Status switch
        {
            CaseClaimStatus.SUCCESS => Result.Success(result.Value!),
            CaseClaimStatus.FORBIDDEN => Result.Forbidden(ErrorCodes.ERR_FORBIDDEN),
            CaseClaimStatus.NOT_HOLDER => Result.Forbidden(ErrorCodes.ERR_MODERATION_NOT_HOLDER),
            CaseClaimStatus.NOT_FOUND => Result.NotFound(ErrorCodes.ERR_MODERATION_CASE_NOT_FOUND),
            CaseClaimStatus.AUDIT_UNAVAILABLE => Result.Unavailable(ErrorCodes.ERR_REVIEW_AUDIT_UNAVAILABLE),
            CaseClaimStatus.NOT_CLAIMED => Result.Conflict(ErrorCodes.ERR_MODERATION_CASE_NOT_CLAIMED),
            CaseClaimStatus.HELD => Result.Conflict(ErrorCodes.ERR_MODERATION_CASE_HELD),
            CaseClaimStatus.NOT_ACTIVE => Result.Conflict(ErrorCodes.ERR_MODERATION_CASE_NOT_ACTIVE),
            CaseClaimStatus.STALE => Result.Conflict(ErrorCodes.ERR_MODERATION_CASE_STALE),
            _ => Result.Conflict(ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT)
        };
    }
}
