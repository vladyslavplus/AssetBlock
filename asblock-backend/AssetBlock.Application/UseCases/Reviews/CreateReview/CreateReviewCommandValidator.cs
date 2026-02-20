using FluentValidation;

namespace AssetBlock.Application.UseCases.Reviews.CreateReview;

internal sealed class CreateReviewCommandValidator : AbstractValidator<CreateReviewCommand>
{
    public CreateReviewCommandValidator()
    {
        RuleFor(c => c.AssetId).NotEmpty();
        RuleFor(c => c.UserId).NotEmpty();
        
        RuleFor(c => c.Rating)
            .InclusiveBetween(1, 5).WithMessage("Rating must be between 1 and 5.");
            
        RuleFor(c => c.Comment)
            .MaximumLength(1000).WithMessage("Comment must not exceed 1000 characters.")
            .When(c => !string.IsNullOrWhiteSpace(c.Comment));
    }
}
