using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.GetSimilarAssets;

internal sealed class GetSimilarAssetsQueryValidator : AbstractValidator<GetSimilarAssetsQuery>
{
    public GetSimilarAssetsQueryValidator()
    {
        RuleFor(q => q.AssetId)
            .NotEmpty().WithMessage("AssetId is required.");
        RuleFor(q => q.Limit)
            .InclusiveBetween(SimilarAssetsConstants.MIN_LIMIT, SimilarAssetsConstants.MAX_LIMIT)
            .WithMessage($"Limit must be between {SimilarAssetsConstants.MIN_LIMIT} and {SimilarAssetsConstants.MAX_LIMIT}.");
        RuleFor(q => q.Mode)
            .Must(mode => mode is SimilarAssetsConstants.MODE_SIMILARITY
                or SimilarAssetsConstants.MODE_POPULARITY)
            .WithMessage($"Mode must be '{SimilarAssetsConstants.MODE_SIMILARITY}' or '{SimilarAssetsConstants.MODE_POPULARITY}'.");
    }
}
