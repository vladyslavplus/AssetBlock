using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Dto.Collections;
using AssetBlock.Domain.Core.Enums;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.GetMyCollections;

internal sealed class GetMyCollectionsQueryValidator : AbstractValidator<GetMyCollectionsQuery>
{
    public GetMyCollectionsQueryValidator()
    {
        RuleFor(q => q.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");

        RuleFor(q => q.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(q => q.Request).SetValidator(new PagedRequestValidator());
                RuleFor(q => q.Request.SortBy)
                    .Must(sortBy => string.IsNullOrWhiteSpace(sortBy) || ListMyCollectionsRequest.AllowedSortBy.Contains(sortBy))
                    .WithMessage($"SortBy must be one of: {string.Join(", ", ListMyCollectionsRequest.AllowedSortBy)}.");
                RuleFor(q => q.Request.Status)
                    .Must(status => string.IsNullOrWhiteSpace(status)
                        || Enum.TryParse<CollectionStatus>(status, ignoreCase: true, out _))
                    .WithMessage("Status must be one of: DRAFT, PUBLISHED, ARCHIVED.");
            });
    }
}
