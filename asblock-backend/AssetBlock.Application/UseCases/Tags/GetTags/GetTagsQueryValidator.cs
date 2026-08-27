using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Dto.Tags;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Tags.GetTags;

internal sealed class GetTagsQueryValidator : AbstractValidator<GetTagsQuery>
{
    public GetTagsQueryValidator()
    {
        RuleFor(q => q.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(q => q.Request).SetValidator(new PagedRequestValidator());
                RuleFor(q => q.Request.SortBy)
                    .Must(sortBy => string.IsNullOrEmpty(sortBy) || GetTagsRequest.AllowedSortBy.Contains(sortBy.ToLowerInvariant()))
                    .WithMessage("SortBy must be one of: id, name.");
            });
    }
}
