using FluentValidation;

namespace AssetBlock.Application.UseCases.Users.MarkNotificationRead;

internal sealed class MarkNotificationReadCommandValidator : AbstractValidator<MarkNotificationReadCommand>
{
    public MarkNotificationReadCommandValidator()
    {
        RuleFor(c => c.UserId)
            .NotEmpty().WithMessage("UserId is required.");

        RuleFor(c => c.NotificationId)
            .NotEmpty().WithMessage("NotificationId is required.");
    }
}
