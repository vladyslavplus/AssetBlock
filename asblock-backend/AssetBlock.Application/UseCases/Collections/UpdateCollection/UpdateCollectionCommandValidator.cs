using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.UpdateCollection;

internal sealed class UpdateCollectionCommandValidator : AbstractValidator<UpdateCollectionCommand>
{
    public UpdateCollectionCommandValidator()
    {
        RuleFor(c => c.Id)
            .NotEmpty().WithMessage("Id is required.");

        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");

        RuleFor(c => c.Title)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Title is required.")
            .Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("Title is required.")
            .Must(t => t.Trim().Length <= CollectionConstants.TITLE_MAX_LENGTH)
            .WithMessage($"Title must not exceed {CollectionConstants.TITLE_MAX_LENGTH} characters.");

        RuleFor(c => c.Description)
            .Must(d => d is null || d.Trim().Length <= CollectionConstants.DESCRIPTION_MAX_LENGTH)
            .WithMessage($"Description must not exceed {CollectionConstants.DESCRIPTION_MAX_LENGTH} characters.");
    }
}
