using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Dto.Collections;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.GetCollections;

internal sealed class GetCollectionsQueryValidator : AbstractValidator<GetCollectionsQuery>
{
    public GetCollectionsQueryValidator()
    {
        RuleFor(q => q.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(q => q.Request).SetValidator(new PagedRequestValidator());
                RuleFor(q => q.Request.SortBy)
                    .Must(sortBy => string.IsNullOrWhiteSpace(sortBy) || ListCollectionsRequest.AllowedSortBy.Contains(sortBy))
                    .WithMessage($"SortBy must be one of: {string.Join(", ", ListCollectionsRequest.AllowedSortBy)}.");
            });
    }
}
