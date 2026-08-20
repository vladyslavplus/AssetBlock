using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.ArchiveCollection;

internal sealed class ArchiveCollectionCommandValidator : AbstractValidator<ArchiveCollectionCommand>
{
    public ArchiveCollectionCommandValidator()
    {
        RuleFor(c => c.Id)
            .NotEmpty().WithMessage("Id is required.");

        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");
    }
}
