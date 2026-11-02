using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Moderation.CoverageReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;
using FluentValidation;
namespace AssetBlock.Application.UseCases.Moderation.AssessCoverage;

public sealed record AssessCoverageCommand(Guid ActorId, Guid RequestId, CoverageAssessmentRequest Input) : IRequest<Result<CoverageReviewSaved>>;
internal sealed class AssessCoverageCommandValidator : AbstractValidator<AssessCoverageCommand>
{
    public AssessCoverageCommandValidator()
    {
        RuleFor(c => c.ActorId).NotEmpty();
        RuleFor(c => c.RequestId).NotEmpty();
        RuleFor(c => c.Input.OperationId).NotEmpty();
        RuleFor(c => c.Input.ExpectedRequestRevision).GreaterThan(0);
        RuleFor(c => c.Input.ExpectedTupleDigest).Matches("^[a-f0-9]{64}$");
        RuleFor(c => c.Input.Outcome).IsInEnum();
        RuleFor(c => c.Input.Rationale).NotEmpty().MaximumLength(ReviewEvidenceLimits.TEXT_LENGTH);
        RuleFor(c => c.Input.ReviewPlan).NotEmpty().MaximumLength(ReviewEvidenceLimits.TEXT_LENGTH);
        RuleFor(c => c.Input.CompletedPlanReference).NotNull().MaximumLength(ReviewEvidenceLimits.PLAN_REFERENCE_LENGTH);
        RuleFor(c => c.Input.Competence).Cascade(CascadeMode.Stop).NotNull().Must(a => a.Count is > 0 and <= ReviewEvidenceLimits.MAX_ITEMS);
        RuleForEach(c => c.Input.Competence).NotEmpty().MaximumLength(60).Matches("^[A-Za-z0-9+#. -]+$");
        RuleFor(c => c.Input.Scope).Cascade(CascadeMode.Stop).NotNull().Must(a => a.Count <= ReviewEvidenceLimits.MAX_ITEMS);
        RuleForEach(c => c.Input.Scope).NotNull().ChildRules(s =>
        {
            s.RuleFor(i => i.Path).NotEmpty().MaximumLength(512);
            s.RuleFor(i => i.Language).NotEmpty().MaximumLength(60);
            s.RuleFor(i => i.RoleInCoreLogic).NotEmpty().MaximumLength(200);
            s.RuleFor(i => i.EvidenceKind).IsInEnum();
        });
        When(c => c.Input.Outcome == CoverageAssessmentOutcome.ACCEPTED, () =>
        {
            RuleFor(c => c.Input.ManageableScope).Equal(true);
            RuleFor(c => c.Input.CompletedPlanReference).NotEmpty();
            RuleFor(c => c.Input.Scope).NotEmpty();
        });
    }
}
internal sealed class AssessCoverageCommandHandler(IModerationFoundationStore store, IUserStore users) : IRequestHandler<AssessCoverageCommand, Result<CoverageReviewSaved>>
{
    public async Task<Result<CoverageReviewSaved>> Handle(AssessCoverageCommand command, CancellationToken ct)
    {
        if (!AppRoles.CanReview((await users.GetPersistedRole(command.ActorId, ct))?.Role))
        { return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN); }
        CoverageAssessmentRequest input = command.Input with { Rationale = command.Input.Rationale.Trim(), ReviewPlan = command.Input.ReviewPlan.Trim(), CompletedPlanReference = command.Input.CompletedPlanReference.Trim() };
        var digest = DraftPayloadJson.ComputeDigest(DraftPayloadJson.Serialize(new { command.RequestId, Input = input with { OperationId = Guid.Empty } }));
        return CoverageReviewResults.Map(await store.AssessCoverage(command.ActorId, command.RequestId, input, digest, ct));
    }
}
