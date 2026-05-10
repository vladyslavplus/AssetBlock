using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Dto.Users;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Users.ListMyPurchases;

internal sealed class GetMyPurchasesQueryValidator : AbstractValidator<GetMyPurchasesQuery>
{
    public GetMyPurchasesQueryValidator()
    {
        RuleFor(q => q.Request)
            .NotNull()
            .WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(q => q.Request).SetValidator(new PagedRequestValidator());
                RuleFor(q => q.Request.SortBy)
                    .Must(sortBy => string.IsNullOrWhiteSpace(sortBy) || ListMyPurchasesRequest.AllowedSortBy.Contains(sortBy))
                    .WithMessage($"SortBy must be one of: {string.Join(", ", ListMyPurchasesRequest.AllowedSortBy)}.");
            });
    }
}
