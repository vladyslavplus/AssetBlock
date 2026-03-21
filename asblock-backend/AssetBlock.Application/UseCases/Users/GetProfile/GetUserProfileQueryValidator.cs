using FluentValidation;

namespace AssetBlock.Application.UseCases.Users.GetProfile;

internal sealed class GetUserProfileQueryValidator : AbstractValidator<GetUserProfileQuery>
{
    public GetUserProfileQueryValidator()
    {
        RuleFor(q => q)
            .Must(q => !string.IsNullOrWhiteSpace(q.Username) || q.CurrentUserId != null)
            .WithMessage("Username is required when not loading the current user profile.");

        When(q => !string.IsNullOrWhiteSpace(q.Username), () =>
        {
            RuleFor(q => q.Username!)
                .MaximumLength(50)
                .WithMessage("Username must not exceed 50 characters.");
        });
    }
}
