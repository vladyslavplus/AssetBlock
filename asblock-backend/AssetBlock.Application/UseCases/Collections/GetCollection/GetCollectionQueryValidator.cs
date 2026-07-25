using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.GetCollection;

internal sealed class GetCollectionQueryValidator : AbstractValidator<GetCollectionQuery>
{
    public GetCollectionQueryValidator()
    {
        RuleFor(q => q.Id)
            .NotEmpty().WithMessage("Id is required.");
    }
}
