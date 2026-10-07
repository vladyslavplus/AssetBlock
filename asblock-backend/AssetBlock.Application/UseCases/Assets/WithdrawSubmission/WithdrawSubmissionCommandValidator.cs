using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.WithdrawSubmission;

internal sealed class WithdrawSubmissionCommandValidator : AbstractValidator<WithdrawSubmissionCommand>
{
    public WithdrawSubmissionCommandValidator()
    {
        RuleFor(c => c.OwnerId).NotEmpty();
        RuleFor(c => c.SubmissionId).NotEmpty();
        RuleFor(c => c.OperationId).NotEmpty();
        RuleFor(c => c.ExpectedCaseRevision).GreaterThan(0);
    }
}
