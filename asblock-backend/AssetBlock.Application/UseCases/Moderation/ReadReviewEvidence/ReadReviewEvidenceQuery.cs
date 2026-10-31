using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Moderation.CoverageReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;
using FluentValidation;
namespace AssetBlock.Application.UseCases.Moderation.ReadReviewEvidence;

public sealed record ReadReviewEvidenceQuery(Guid ActorId, Guid ScopeId, bool CoverageScope, RestrictedEvidenceKind Kind, Guid? FindingId) : IRequest<Result<RestrictedEvidenceDto>>;
internal sealed class ReadReviewEvidenceQueryValidator : AbstractValidator<ReadReviewEvidenceQuery>
{
    public ReadReviewEvidenceQueryValidator() { RuleFor(c => c.ActorId).NotEmpty(); RuleFor(c => c.ScopeId).NotEmpty(); RuleFor(c => c.Kind).IsInEnum(); }
}
internal sealed class ReadReviewEvidenceQueryHandler(IModerationFoundationStore store, IUserStore users) : IRequestHandler<ReadReviewEvidenceQuery, Result<RestrictedEvidenceDto>>
{
    public async Task<Result<RestrictedEvidenceDto>> Handle(ReadReviewEvidenceQuery command, CancellationToken ct)
    {
        if (!AppRoles.CanReview((await users.GetPersistedRole(command.ActorId, ct))?.Role))
        { return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN); }
        return CoverageReviewResults.Map(await store.ReadRestrictedEvidence(command.ActorId, command.ScopeId, command.CoverageScope, command.Kind, command.FindingId, ct));
    }
}
