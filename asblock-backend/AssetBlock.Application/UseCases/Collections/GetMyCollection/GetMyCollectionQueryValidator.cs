using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.GetMyCollection;

internal sealed class GetMyCollectionQueryValidator : AbstractValidator<GetMyCollectionQuery>
{
    public GetMyCollectionQueryValidator()
    {
        RuleFor(q => q.Id)
            .NotEmpty().WithMessage("Id is required.");

        RuleFor(q => q.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");
    }
}
