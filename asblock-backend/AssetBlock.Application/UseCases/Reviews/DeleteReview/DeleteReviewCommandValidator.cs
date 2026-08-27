using FluentValidation;

namespace AssetBlock.Application.UseCases.Reviews.DeleteReview;

internal sealed class DeleteReviewCommandValidator : AbstractValidator<DeleteReviewCommand>
{
    public DeleteReviewCommandValidator()
    {
        RuleFor(c => c.Id)
            .NotEmpty().WithMessage("Id is required.");
    }
}
