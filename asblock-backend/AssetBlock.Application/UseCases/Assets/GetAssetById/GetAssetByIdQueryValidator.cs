using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.GetAssetById;

internal sealed class GetAssetByIdQueryValidator : AbstractValidator<GetAssetByIdQuery>
{
    public GetAssetByIdQueryValidator()
    {
        RuleFor(q => q.Id)
            .NotEmpty().WithMessage("Id is required.");
    }
}
