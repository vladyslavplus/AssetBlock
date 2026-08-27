using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.GetAssetVersions;

internal sealed class GetAssetVersionsQueryValidator : AbstractValidator<GetAssetVersionsQuery>
{
    public GetAssetVersionsQueryValidator()
    {
        RuleFor(q => q.AssetId)
            .NotEmpty().WithMessage("AssetId is required.");

        RuleFor(q => q.RequesterUserId)
            .Must(id => id is null || id != Guid.Empty)
            .WithMessage("RequesterUserId cannot be empty when specified.");
    }
}
