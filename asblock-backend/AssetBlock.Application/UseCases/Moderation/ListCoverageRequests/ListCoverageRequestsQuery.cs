using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Moderation.CoverageReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using FluentValidation;
namespace AssetBlock.Application.UseCases.Moderation.ListCoverageRequests;

public sealed record ListCoverageRequestsQuery(Guid ActorId, int Page) : IRequest<Result<CoverageQueuePage>>;
internal sealed class ListCoverageRequestsQueryValidator : AbstractValidator<ListCoverageRequestsQuery>
{
    public ListCoverageRequestsQueryValidator() { RuleFor(c => c.ActorId).NotEmpty(); RuleFor(c => c.Page).InclusiveBetween(1, 10000); }
}
internal sealed class ListCoverageRequestsQueryHandler(IModerationFoundationStore store, IUserStore users) : IRequestHandler<ListCoverageRequestsQuery, Result<CoverageQueuePage>>
{
    public async Task<Result<CoverageQueuePage>> Handle(ListCoverageRequestsQuery command, CancellationToken ct)
    {
        if (!AppRoles.CanReview((await users.GetPersistedRole(command.ActorId, ct))?.Role))
        { return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN); }
        return CoverageReviewResults.Map(await store.GetCoverageQueue(command.ActorId, command.Page, ct));
    }
}
