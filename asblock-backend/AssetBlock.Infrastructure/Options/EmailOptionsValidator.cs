using System.Net.Mail;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    private const int MIN_TIMEOUT_SECONDS = 1;
    internal const int MAX_TIMEOUT_SECONDS = 120;
    private const string CANONICAL_PROVIDER = "Smtp";

    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var failures = new List<string>();

        if (OptionsValidation.IsMissingOrPlaceholder(options.Provider)
            || !string.Equals(options.Provider.Trim(), CANONICAL_PROVIDER, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"Email:Provider must be '{CANONICAL_PROVIDER}'.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.FromName))
        {
            failures.Add("Email:FromName must be non-empty.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.FromAddress))
        {
            failures.Add("Email:FromAddress must be non-empty.");
        }
        else if (!TryValidateMailbox(options.FromAddress))
        {
            failures.Add("Email:FromAddress must be a valid mailbox address.");
        }

        if (!OptionsValidation.IsHttpOrigin(options.PublicAppBaseUrl))
        {
            failures.Add(
                "Email:PublicAppBaseUrl must be an absolute http or https origin without user-info, path, query, or fragment.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.MessageIdDomain))
        {
            failures.Add("Email:MessageIdDomain must be non-empty.");
        }
        else if (!IsSafeMessageIdDomain(options.MessageIdDomain))
        {
            failures.Add("Email:MessageIdDomain must be a safe hostname/domain (e.g. mail.localhost).");
        }

        var smtp = options.Smtp;
        if (OptionsValidation.IsMissingOrPlaceholder(smtp.Host))
        {
            failures.Add("Email:Smtp:Host must be non-empty.");
        }

        if (smtp.Port is < 1 or > 65535)
        {
            failures.Add("Email:Smtp:Port must be between 1 and 65535.");
        }

        if (!Enum.IsDefined(smtp.Security))
        {
            failures.Add("Email:Smtp:Security must be a valid SmtpSecurityMode value.");
        }

        if (smtp.TimeoutSeconds is < MIN_TIMEOUT_SECONDS or > MAX_TIMEOUT_SECONDS)
        {
            failures.Add(
                $"Email:Smtp:TimeoutSeconds must be between {MIN_TIMEOUT_SECONDS} and {MAX_TIMEOUT_SECONDS}.");
        }

        ValidateSmtpCredentials(smtp.Username, smtp.Password, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateSmtpCredentials(string? username, string? password, List<string> failures)
    {
        var usernameRaw = username?.Trim() ?? string.Empty;
        var passwordRaw = password?.Trim() ?? string.Empty;
        var usernameEmpty = usernameRaw.Length == 0;
        var passwordEmpty = passwordRaw.Length == 0;
        var usernamePlaceholder = !usernameEmpty && OptionsValidation.IsMissingOrPlaceholder(usernameRaw);
        var passwordPlaceholder = !passwordEmpty && OptionsValidation.IsMissingOrPlaceholder(passwordRaw);

        if (usernamePlaceholder || passwordPlaceholder)
        {
            failures.Add("Email:Smtp:Username and Email:Smtp:Password must not be placeholders.");
            return;
        }

        if (usernameEmpty != passwordEmpty)
        {
            failures.Add("Email:Smtp:Username and Email:Smtp:Password must both be set or both empty.");
        }
    }

    private static bool TryValidateMailbox(string address)
    {
        try
        {
            _ = new MailAddress(address.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSafeMessageIdDomain(string domain)
    {
        var trimmed = domain.Trim();
        if (trimmed.Length == 0 || OptionsValidation.IsMissingOrPlaceholder(trimmed))
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch)
                || char.IsControl(ch)
                || ch is '@' or '<' or '>' or '/' or '\\' or ':' or '?' or '#' or '[' or ']')
            {
                return false;
            }
        }

        var hostKind = Uri.CheckHostName(trimmed);
        return hostKind is UriHostNameType.Dns or UriHostNameType.Basic;
    }
}
