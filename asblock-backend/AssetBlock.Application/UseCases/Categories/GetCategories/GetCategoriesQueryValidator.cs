using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Dto.Categories;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Categories.GetCategories;

internal sealed class GetCategoriesQueryValidator : AbstractValidator<GetCategoriesQuery>
{
    public GetCategoriesQueryValidator()
    {
        RuleFor(q => q.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(q => q.Request).SetValidator(new PagedRequestValidator());
                RuleFor(q => q.Request.SortBy)
                    .Must(sortBy => string.IsNullOrEmpty(sortBy) || GetCategoriesRequest.AllowedSortBy.Contains(sortBy))
                    .WithMessage("SortBy must be one of: Name, Slug, Id.");
            });
    }
}
