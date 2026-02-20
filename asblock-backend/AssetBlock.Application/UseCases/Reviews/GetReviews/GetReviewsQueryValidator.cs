using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Dto.Reviews;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Reviews.GetReviews;

internal sealed class GetReviewsQueryValidator : AbstractValidator<GetReviewsQuery>
{
    public GetReviewsQueryValidator()
    {
        RuleFor(q => q.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(q => q.Request).SetValidator(new PagedRequestValidator());
                RuleFor(q => q.Request.SortBy)
                    .Must(sortBy => string.IsNullOrEmpty(sortBy) || GetReviewsRequest.AllowedSortBy.Contains(sortBy))
                    .WithMessage($"SortBy must be one of: {string.Join(", ", GetReviewsRequest.AllowedSortBy)}.");
            });
    }
}
