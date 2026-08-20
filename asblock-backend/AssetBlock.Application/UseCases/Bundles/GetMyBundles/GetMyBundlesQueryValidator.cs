using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Dto.Bundles;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Bundles.GetMyBundles;

internal sealed class GetMyBundlesQueryValidator : AbstractValidator<GetMyBundlesQuery>
{
    public GetMyBundlesQueryValidator()
    {
        RuleFor(q => q.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");

        RuleFor(q => q.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(q => q.Request).SetValidator(new PagedRequestValidator());
                RuleFor(q => q.Request.SortBy)
                    .Must(sortBy => string.IsNullOrWhiteSpace(sortBy) || ListMyBundlesRequest.AllowedSortBy.Contains(sortBy))
                    .WithMessage($"SortBy must be one of: {string.Join(", ", ListMyBundlesRequest.AllowedSortBy)}.");
            });
    }
}
