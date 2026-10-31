using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Moderation.CaseAssignment;

public sealed record GetModeratorQueueQuery(Guid ActorId, ModerationSubmissionState? State, CaseClaimFilter ClaimFilter, int PageSize, string? Cursor) : IRequest<Result<ModeratorQueuePage>>;
internal sealed class GetModeratorQueueQueryValidator : AbstractValidator<GetModeratorQueueQuery>
{
    public GetModeratorQueueQueryValidator()
    {
        RuleFor(x => x.ActorId).NotEmpty();
        RuleFor(x => x.State).Must(x => x is null or ModerationSubmissionState.SUBMITTED or ModerationSubmissionState.IN_REVIEW);
        RuleFor(x => x.ClaimFilter).IsInEnum();
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Cursor).MaximumLength(2048);
    }
}
internal sealed class GetModeratorQueueQueryHandler(IModerationFoundationStore store, IUserStore users) : IRequestHandler<GetModeratorQueueQuery, Result<ModeratorQueuePage>>
{
    public async Task<Result<ModeratorQueuePage>> Handle(GetModeratorQueueQuery query, CancellationToken ct)
    {
        if (!AppRoles.CanReview((await users.GetPersistedRole(query.ActorId, ct))?.Role))
        { return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN); }
        ReviewAccessResult<ModeratorQueuePage> result = await store.GetModeratorQueue(new(query.ActorId, query.State, query.ClaimFilter, query.PageSize, query.Cursor), ct);
        return result.Status switch
        {
            ReviewAccessStatus.SUCCESS => Result.Success(result.Value!),
            ReviewAccessStatus.FORBIDDEN => Result.Forbidden(ErrorCodes.ERR_FORBIDDEN),
            _ => ResultError.Error<ModeratorQueuePage>(ErrorCodes.ERR_VALIDATION_FAILED)
        };
    }
}
