using FluentValidation;

namespace AssetBlock.Application.UseCases.Users.UpdateProfile;

internal sealed class UpdateUserProfileCommandValidator : AbstractValidator<UpdateUserProfileCommand>
{
    public UpdateUserProfileCommandValidator()
    {
        When(c => c.Username is not null, () =>
        {
            RuleFor(c => c.Username!)
                .NotEmpty()
                .WithMessage("Username cannot be empty.")
                .MaximumLength(50)
                .WithMessage("Username must not exceed 50 characters.");
        });

        When(c => c.AvatarUrl is not null, () =>
        {
            RuleFor(c => c.AvatarUrl!)
                .MaximumLength(500)
                .WithMessage("Avatar URL must not exceed 500 characters.")
                .Must(BeValidHttpOrHttpsUrl)
                .WithMessage("Avatar URL must be a valid http or https address.");
        });

        When(c => c.Bio is not null, () =>
        {
            RuleFor(c => c.Bio!)
                .MaximumLength(1000)
                .WithMessage("Bio must not exceed 1000 characters.");
        });
    }

    private static bool BeValidHttpOrHttpsUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }
}
