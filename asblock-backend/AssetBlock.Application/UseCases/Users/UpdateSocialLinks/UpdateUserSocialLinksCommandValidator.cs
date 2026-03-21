using FluentValidation;

namespace AssetBlock.Application.UseCases.Users.UpdateSocialLinks;

internal sealed class UpdateUserSocialLinksCommandValidator : AbstractValidator<UpdateUserSocialLinksCommand>
{
    private const int MAX_LINKS = 32;
    private const int MAX_URL_LENGTH = 500;

    public UpdateUserSocialLinksCommandValidator()
    {
        RuleFor(c => c.Links)
            .NotNull()
            .WithMessage("Links are required.");

        When(c => c.Links is not null, () =>
        {
            RuleFor(c => c.Links!)
                .Must(l => l.Count <= MAX_LINKS)
                .WithMessage($"At most {MAX_LINKS} social links are allowed.");

            RuleFor(c => c.Links!)
                .Must(links => links.Select(x => x.PlatformId).Distinct().Count() == links.Count)
                .WithMessage("Each platform may appear at most once.");

            RuleForEach(c => c.Links!).ChildRules(link =>
            {
                link.RuleFor(l => l.PlatformId)
                    .NotEqual(Guid.Empty)
                    .WithMessage("Platform id is required.");

                link.RuleFor(l => l.Url)
                    .NotEmpty()
                    .WithMessage("URL is required.")
                    .MaximumLength(MAX_URL_LENGTH)
                    .WithMessage($"URL must not exceed {MAX_URL_LENGTH} characters.")
                    .Must(BeValidHttpOrHttpsUrl)
                    .WithMessage("URL must be a valid http or https address.");
            });
        });
    }

    private static bool BeValidHttpOrHttpsUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }
}
