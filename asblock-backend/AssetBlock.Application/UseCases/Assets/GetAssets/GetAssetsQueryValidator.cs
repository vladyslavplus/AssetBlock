using AssetBlock.Application.Common;
using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Dto.Assets;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.GetAssets;

internal sealed class GetAssetsQueryValidator : AbstractValidator<GetAssetsQuery>
{
    public GetAssetsQueryValidator()
    {
        RuleFor(q => q.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(q => q.Request).SetValidator(new PagedRequestValidator());
                RuleFor(q => q.Request.Search)
                    .Must(CatalogSearchNormalization.BeWithinUnicodeScalarLimit)
                    .WithMessage("Search query must not exceed 256 Unicode scalars.")
                    .Must(CatalogSearchNormalization.NotContainInvalidControlCharacters)
                    .WithMessage("Search query must not contain invalid control characters.")
                    .When(q => !string.IsNullOrEmpty(q.Request.Search));
                RuleFor(q => q.Request.SortBy)
                    .Must(sortBy => string.IsNullOrEmpty(sortBy) || GetAssetsRequest.AllowedSortBy.Contains(sortBy))
                    .WithMessage("SortBy must be one of: Title, Price, CreatedAt, Id.");
                RuleFor(q => q.Request.MinPrice)
                    .GreaterThanOrEqualTo(0).When(q => q.Request.MinPrice.HasValue)
                    .WithMessage("MinPrice must be >= 0.");
                RuleFor(q => q.Request.MaxPrice)
                    .GreaterThanOrEqualTo(0).When(q => q.Request.MaxPrice.HasValue)
                    .WithMessage("MaxPrice must be >= 0.");
                RuleFor(q => q.Request)
                    .Must(r => !r.MinPrice.HasValue || !r.MaxPrice.HasValue || r.MinPrice <= r.MaxPrice)
                    .WithMessage("MinPrice must be less than or equal to MaxPrice.");
            });
    }
}
