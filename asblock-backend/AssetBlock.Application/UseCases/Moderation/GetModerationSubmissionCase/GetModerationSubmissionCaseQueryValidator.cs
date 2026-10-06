using FluentValidation;

namespace AssetBlock.Application.UseCases.Moderation.GetModerationSubmissionCase;

internal sealed class GetModerationSubmissionCaseQueryValidator : AbstractValidator<GetModerationSubmissionCaseQuery>
{
    public GetModerationSubmissionCaseQueryValidator()
    {
        RuleFor(q => q.ActorUserId).NotEmpty();
        RuleFor(q => q.SubmissionId).NotEmpty();
    }
}
