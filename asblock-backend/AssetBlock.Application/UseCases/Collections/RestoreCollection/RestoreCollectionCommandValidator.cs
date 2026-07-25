using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.RestoreCollection;

internal sealed class RestoreCollectionCommandValidator : AbstractValidator<RestoreCollectionCommand>
{
    public RestoreCollectionCommandValidator()
    {
        RuleFor(c => c.Id)
            .NotEmpty().WithMessage("Id is required.");

        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");
    }
}
