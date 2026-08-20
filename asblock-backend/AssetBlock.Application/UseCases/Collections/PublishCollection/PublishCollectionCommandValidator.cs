using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.PublishCollection;

internal sealed class PublishCollectionCommandValidator : AbstractValidator<PublishCollectionCommand>
{
    public PublishCollectionCommandValidator()
    {
        RuleFor(c => c.Id)
            .NotEmpty().WithMessage("Id is required.");

        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");
    }
}
