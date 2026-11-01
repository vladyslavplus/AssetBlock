using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Application.UseCases.Moderation.GetModerationSubmissionCase;

internal sealed class GetModerationSubmissionCaseQueryHandler(
    IModerationFoundationStore moderationFoundationStore,
    IUserStore userStore) : IRequestHandler<GetModerationSubmissionCaseQuery, Result<ModerationCaseSummary>>
{
    public async Task<Result<ModerationCaseSummary>> Handle(
        GetModerationSubmissionCaseQuery request,
        CancellationToken cancellationToken)
    {
        UserPersistedRole? role = await userStore.GetPersistedRole(request.ActorUserId, cancellationToken);
        if (!AppRoles.CanReview(role?.Role))
        {
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        ModerationCaseAccessResult access = await moderationFoundationStore.GetCaseSummaryForModerator(
            request.ActorUserId,
            request.SubmissionId,
            cancellationToken);

        return access.Status switch
        {
            ModerationCaseAccessStatus.APPEAL_REVIEWER_EXCLUDED => Result.Forbidden(ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED),
            ModerationCaseAccessStatus.DENIED_ROLE or ModerationCaseAccessStatus.SELF_OWNED_DENIED => Result.Forbidden(ErrorCodes.ERR_FORBIDDEN),
            ModerationCaseAccessStatus.SECURITY_RESTRICTED => Result.Forbidden(ErrorCodes.ERR_EVIDENCE_SECURITY_RESTRICTED),
            ModerationCaseAccessStatus.AUDIT_UNAVAILABLE => Result.Unavailable(ErrorCodes.ERR_EVIDENCE_AUDIT_UNAVAILABLE),
            ModerationCaseAccessStatus.NOT_FOUND => Result.NotFound(ErrorCodes.ERR_MODERATION_CASE_NOT_FOUND),
            _ => Result.Success(access.Summary!)
        };
    }
}
