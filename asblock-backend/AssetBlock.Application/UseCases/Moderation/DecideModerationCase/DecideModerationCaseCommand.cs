using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Moderation.DecideModerationCase;

public sealed record DecideModerationCaseCommand(Guid ActorId, Guid SubmissionId, Guid OperationId, DecisionBasis Basis,
    ModerationDecisionOutcome Outcome, string Rationale, IReadOnlyList<FindingDispositionInput> Dispositions,
    string? ManualPlanCompletionReference) : IRequest<Result<ModerationDecisionSaved>>;

internal sealed class DecideModerationCaseCommandValidator : AbstractValidator<DecideModerationCaseCommand>
{
    public DecideModerationCaseCommandValidator()
    {
        RuleFor(x => x.ActorId).NotEmpty();
        RuleFor(x => x.SubmissionId).NotEmpty();
        RuleFor(x => x.OperationId).NotEmpty();
        RuleFor(x => x.Basis).NotNull();
        When(x => x.Basis is not null, () =>
        {
            RuleFor(x => x.Basis.VersionId).NotEmpty();
            RuleFor(x => x.Basis.AnalysisReportId).NotEmpty();
            RuleFor(x => x.Basis.ContentSha256).NotEmpty().Matches("^[a-f0-9]{64}$");
            RuleFor(x => x.Basis.PolicyVersion).NotEmpty().MaximumLength(64);
            RuleFor(x => x.Basis.DeclarationRevision).GreaterThan(0);
            RuleFor(x => x.Basis.MaterialMetadataRevision).GreaterThan(0);
            RuleFor(x => x.Basis.SellerEvidenceRevision).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Basis.CaseRevision).GreaterThan(0);
        });
        RuleFor(x => x.Outcome).IsInEnum();
        RuleFor(x => x.Rationale).NotEmpty().MaximumLength(2000).Must(x => x is null || !x.Contains('\0'));
        RuleFor(x => x.ManualPlanCompletionReference).MaximumLength(ReviewEvidenceLimits.PLAN_REFERENCE_LENGTH).Must(x => x is null || !x.Contains('\0'));
        RuleFor(x => x.Dispositions).Cascade(CascadeMode.Stop).NotNull().Must(x => x.Count <= SellerEvidenceLimits.MAX_RESPONSES);
        RuleForEach(x => x.Dispositions).NotNull().ChildRules(d => { d.RuleFor(x => x.FindingId).NotEmpty(); d.RuleFor(x => x.Disposition).IsInEnum(); d.RuleFor(x => x.Rationale).NotEmpty().MaximumLength(SellerEvidenceLimits.TEXT_MAX_LENGTH).Must(x => x is null || !x.Contains('\0')); });
    }
}

internal sealed class DecideModerationCaseCommandHandler(IModerationFoundationStore store, IUserStore users) : IRequestHandler<DecideModerationCaseCommand, Result<ModerationDecisionSaved>>
{
    public async Task<Result<ModerationDecisionSaved>> Handle(DecideModerationCaseCommand command, CancellationToken ct)
    {
        if (!AppRoles.CanReview((await users.GetPersistedRole(command.ActorId, ct))?.Role))
        { return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN); }
        FindingDispositionInput[] dispositions = command.Dispositions.OrderBy(d => d.FindingId).Select(d => d with { Rationale = d.Rationale.Trim() }).ToArray();
        DecideModerationCaseCommand normalized = command with { Rationale = command.Rationale.Trim(), Dispositions = dispositions, ManualPlanCompletionReference = command.ManualPlanCompletionReference?.Trim() };
        var digest = DraftPayloadJson.ComputeDigest(DraftPayloadJson.Serialize(new { normalized.SubmissionId, normalized.Basis, normalized.Outcome, normalized.Rationale, normalized.Dispositions, normalized.ManualPlanCompletionReference }));
        ModerationDecisionResult result = await store.DecideModerationCase(new(command.ActorId, command.SubmissionId, command.OperationId, command.Basis,
            command.Outcome, normalized.Rationale, dispositions, normalized.ManualPlanCompletionReference, digest), ct);
        return result.Status switch
        {
            ModerationDecisionStatus.SUCCEEDED => Result.Success(result.Saved!),
            ModerationDecisionStatus.FORBIDDEN or ModerationDecisionStatus.NOT_HOLDER => Result.Forbidden(ErrorCodes.ERR_FORBIDDEN),
            ModerationDecisionStatus.NOT_FOUND => Result.NotFound(ErrorCodes.ERR_MODERATION_CASE_NOT_FOUND),
            ModerationDecisionStatus.AUDIT_UNAVAILABLE => Result.Unavailable(ErrorCodes.ERR_REVIEW_AUDIT_UNAVAILABLE),
            _ when result.ReasonCode == ErrorCodes.ERR_VALIDATION_FAILED => ResultError.Error<ModerationDecisionSaved>(ErrorCodes.ERR_VALIDATION_FAILED),
            _ => Result.Conflict(result.ReasonCode ?? ErrorCodes.ERR_MODERATION_CASE_STALE)
        };
    }
}
