using FluentValidation;

namespace AssetBlock.Application.UseCases.Tags.DeleteTag;

internal sealed class DeleteTagCommandValidator : AbstractValidator<DeleteTagCommand>
{
    public DeleteTagCommandValidator()
    {
        RuleFor(c => c.Id)
            .NotEmpty().WithMessage("Id is required.");
    }
}
