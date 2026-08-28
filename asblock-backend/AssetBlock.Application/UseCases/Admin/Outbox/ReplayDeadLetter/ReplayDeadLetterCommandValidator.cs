using FluentValidation;

namespace AssetBlock.Application.UseCases.Admin.Outbox.ReplayDeadLetter;

internal sealed class ReplayDeadLetterCommandValidator : AbstractValidator<ReplayDeadLetterCommand>
{
    public ReplayDeadLetterCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}
