using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Dto.Notifications;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Users.ListNotifications;

internal sealed class GetNotificationsQueryValidator : AbstractValidator<GetNotificationsQuery>
{
    public GetNotificationsQueryValidator()
    {
        RuleFor(q => q.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(q => q.Request).SetValidator(new PagedRequestValidator());
                RuleFor(q => q.Request.SortBy)
                    .Must(sortBy => string.IsNullOrEmpty(sortBy) || GetNotificationsRequest.AllowedSortBy.Contains(sortBy))
                    .WithMessage($"SortBy must be one of: {string.Join(", ", GetNotificationsRequest.AllowedSortBy)}.");
            });
    }
}
