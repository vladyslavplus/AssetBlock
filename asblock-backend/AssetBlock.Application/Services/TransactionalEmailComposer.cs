using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Encodings.Web;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.Services;

/// <summary>Builds provider-neutral transactional email payloads (no SMTP / DB / HTTP).</summary>
public sealed class TransactionalEmailComposer(IOptions<EmailOptions> emailOptions) : ITransactionalEmailComposer
{
    private const string LIBRARY_PATH = "/library";
    private const string SELLER_LISTINGS_PATH = "/sell";

    private readonly EmailOptions _options = emailOptions.Value;

    public EmailDispatchPayload CreatePurchaseReceipt(
        string recipientAddress,
        Guid recipientUserId,
        string assetTitle,
        DateTimeOffset purchasedAt)
    {
        ValidateRecipient(recipientAddress);
        var title = NormalizeTitle(assetTitle);
        var when = FormatTimestamp(purchasedAt);
        var libraryUrl = BuildFixedAppUrl(LIBRARY_PATH);

        var subject = NormalizeSubject($"Purchase receipt: {title}");
        var text = new StringBuilder()
            .AppendLine("Thanks for your purchase on AssetBlock.")
            .AppendLine()
            .AppendLine($"Asset: {title}")
            .AppendLine($"Purchased at (UTC): {when}")
            .AppendLine()
            .AppendLine($"Open your library: {libraryUrl}")
            .ToString();

        var safeTitle = HtmlEncoder.Default.Encode(title);
        var safeWhen = HtmlEncoder.Default.Encode(when);
        var safeLibraryUrl = HtmlEncoder.Default.Encode(libraryUrl);
        var html = WrapHtmlLayout(
            "Purchase receipt",
            $"""
            <p>Thanks for your purchase on AssetBlock.</p>
            <p><strong>Asset:</strong> {safeTitle}</p>
            <p><strong>Purchased at (UTC):</strong> {safeWhen}</p>
            <p><a href="{safeLibraryUrl}">Open your library</a></p>
            """);

        EnsureBounded(subject, text, html);
        return new EmailDispatchPayload(
            recipientAddress.Trim(),
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            subject,
            text,
            html);
    }

    public EmailDispatchPayload CreateAssetSold(
        string recipientAddress,
        Guid recipientUserId,
        string assetTitle,
        DateTimeOffset purchasedAt)
    {
        ValidateRecipient(recipientAddress);
        var title = NormalizeTitle(assetTitle);
        var when = FormatTimestamp(purchasedAt);
        var sellUrl = BuildFixedAppUrl(SELLER_LISTINGS_PATH);

        var subject = NormalizeSubject($"Asset sold: {title}");
        var text = new StringBuilder()
            .AppendLine("One of your assets was purchased on AssetBlock.")
            .AppendLine()
            .AppendLine($"Asset: {title}")
            .AppendLine($"Sold at (UTC): {when}")
            .AppendLine()
            .AppendLine($"Open your listings: {sellUrl}")
            .ToString();

        var safeTitle = HtmlEncoder.Default.Encode(title);
        var safeWhen = HtmlEncoder.Default.Encode(when);
        var safeSellUrl = HtmlEncoder.Default.Encode(sellUrl);
        var html = WrapHtmlLayout(
            "Asset sold",
            $"""
            <p>One of your assets was purchased on AssetBlock.</p>
            <p><strong>Asset:</strong> {safeTitle}</p>
            <p><strong>Sold at (UTC):</strong> {safeWhen}</p>
            <p><a href="{safeSellUrl}">Open your listings</a></p>
            """);

        EnsureBounded(subject, text, html);
        return new EmailDispatchPayload(
            recipientAddress.Trim(),
            recipientUserId,
            EmailTemplateKind.ASSET_SOLD,
            subject,
            text,
            html);
    }

    public EmailDispatchPayload CreatePasswordChangedNotice(string recipientAddress, Guid recipientUserId)
    {
        ValidateRecipient(recipientAddress);
        var subject = NormalizeSubject("Your AssetBlock password has been changed");
        var text = new StringBuilder()
            .AppendLine("Your AssetBlock account password was recently changed.")
            .AppendLine()
            .AppendLine("If you made this change, no action is needed.")
            .AppendLine("If you did not change your password, please contact support immediately.")
            .ToString();
        var html = WrapHtmlLayout(
            "Password changed",
            """
            <p>Your AssetBlock account password was recently changed.</p>
            <p>If you made this change, no action is needed.</p>
            <p>If you did not change your password, please contact support immediately.</p>
            """);
        EnsureBounded(subject, text, html);
        return new EmailDispatchPayload(
            recipientAddress.Trim(),
            recipientUserId,
            EmailTemplateKind.PASSWORD_CHANGED_NOTICE,
            subject,
            text,
            html);
    }

    public EmailDispatchPayload CreateEmailChangedNotice(string recipientAddress, Guid recipientUserId)
    {
        ValidateRecipient(recipientAddress);
        var subject = NormalizeSubject("Your AssetBlock email address has been changed");
        var text = new StringBuilder()
            .AppendLine("Your AssetBlock account email address was recently changed.")
            .AppendLine()
            .AppendLine("If you made this change, no action is needed.")
            .AppendLine("If you did not make this change, please contact support immediately.")
            .ToString();
        var html = WrapHtmlLayout(
            "Email address changed",
            """
            <p>Your AssetBlock account email address was recently changed.</p>
            <p>If you made this change, no action is needed.</p>
            <p>If you did not make this change, please contact support immediately.</p>
            """);
        EnsureBounded(subject, text, html);
        return new EmailDispatchPayload(
            recipientAddress.Trim(),
            recipientUserId,
            EmailTemplateKind.EMAIL_CHANGED_NOTICE,
            subject,
            text,
            html);
    }

    public EmailMessage CreateEmailVerification(string recipientAddress, Guid recipientUserId, string actionUrl)
    {
        ValidateRecipient(recipientAddress);
        ValidateActionUrl(actionUrl);
        var safeUrl = HtmlEncoder.Default.Encode(actionUrl);
        var subject = NormalizeSubject("Verify your AssetBlock email");
        var text = new StringBuilder()
            .AppendLine("Confirm your email address for AssetBlock.")
            .AppendLine()
            .AppendLine($"Open this link to verify: {actionUrl}")
            .AppendLine()
            .AppendLine("If you did not create an account, you can ignore this email.")
            .ToString();
        var html = WrapHtmlLayout(
            "Verify your email",
            $"""
            <p>Confirm your email address for AssetBlock.</p>
            <p><a href="{safeUrl}">Verify email</a></p>
            <p>If you did not create an account, you can ignore this email.</p>
            """);
        EnsureBounded(subject, text, html);
        return new EmailMessage(
            recipientAddress.Trim(),
            recipientUserId,
            subject,
            text,
            html,
            EmailTemplateKind.EMAIL_VERIFICATION,
            MessageId: "pending");
    }

    public EmailMessage CreatePasswordReset(string recipientAddress, Guid recipientUserId, string actionUrl)
    {
        ValidateRecipient(recipientAddress);
        ValidateActionUrl(actionUrl);
        var safeUrl = HtmlEncoder.Default.Encode(actionUrl);
        var subject = NormalizeSubject("Reset your AssetBlock password");
        var text = new StringBuilder()
            .AppendLine("We received a request to reset your AssetBlock password.")
            .AppendLine()
            .AppendLine($"Open this link to choose a new password: {actionUrl}")
            .AppendLine()
            .AppendLine("If you did not request a reset, you can ignore this email.")
            .ToString();
        var html = WrapHtmlLayout(
            "Reset your password",
            $"""
            <p>We received a request to reset your AssetBlock password.</p>
            <p><a href="{safeUrl}">Choose a new password</a></p>
            <p>If you did not request a reset, you can ignore this email.</p>
            """);
        EnsureBounded(subject, text, html);
        return new EmailMessage(
            recipientAddress.Trim(),
            recipientUserId,
            subject,
            text,
            html,
            EmailTemplateKind.PASSWORD_RESET,
            MessageId: "pending");
    }

    public EmailMessage CreateEmailChangeConfirmation(string recipientAddress, Guid recipientUserId, string actionUrl)
    {
        ValidateRecipient(recipientAddress);
        ValidateActionUrl(actionUrl);
        var safeUrl = HtmlEncoder.Default.Encode(actionUrl);
        var subject = NormalizeSubject("Confirm your new AssetBlock email");        var text = new StringBuilder()
            .AppendLine("Confirm this email address for your AssetBlock account.")
            .AppendLine()
            .AppendLine($"Open this link to confirm: {actionUrl}")
            .AppendLine()
            .AppendLine("If you did not request an email change, you can ignore this email.")
            .ToString();
        var html = WrapHtmlLayout(
            "Confirm your new email",
            $"""
            <p>Confirm this email address for your AssetBlock account.</p>
            <p><a href="{safeUrl}">Confirm email change</a></p>
            <p>If you did not request an email change, you can ignore this email.</p>
            """);
        EnsureBounded(subject, text, html);
        return new EmailMessage(
            recipientAddress.Trim(),
            recipientUserId,
            subject,
            text,
            html,
            EmailTemplateKind.EMAIL_CHANGE_CONFIRMATION,
            MessageId: "pending");
    }

    private static void ValidateActionUrl(string actionUrl)
    {
        if (string.IsNullOrWhiteSpace(actionUrl)
            || !Uri.TryCreate(actionUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Action URL must be an absolute http(s) URL.", nameof(actionUrl));
        }
    }

    private static void ValidateRecipient(string recipientAddress)
    {
        if (string.IsNullOrWhiteSpace(recipientAddress))
        {
            throw new ArgumentException("Recipient address is required.", nameof(recipientAddress));
        }

        try
        {
            _ = new MailAddress(recipientAddress.Trim());
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Recipient address is not a valid mailbox.", nameof(recipientAddress), ex);
        }
    }

    private static string NormalizeTitle(string assetTitle)
    {
        if (string.IsNullOrWhiteSpace(assetTitle))
        {
            throw new ArgumentException("Asset title is required.", nameof(assetTitle));
        }

        return assetTitle.Trim();
    }

    private static string FormatTimestamp(DateTimeOffset purchasedAt)
    {
        if (purchasedAt == default)
        {
            throw new ArgumentException("Purchase timestamp is required.", nameof(purchasedAt));
        }

        return purchasedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
    }

    private string BuildFixedAppUrl(string absolutePath)
    {
        var configured = _options.PublicAppBaseUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException(
                "Email:PublicAppBaseUrl must be a configured absolute http(s) origin.");
        }

        return uri.GetLeftPart(UriPartial.Authority) + absolutePath;
    }

    private static string NormalizeSubject(string subject)
    {
        var normalized = subject
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Email subject must be non-empty after normalization.");
        }

        if (normalized.Length > EmailContentLimits.MAX_SUBJECT_LENGTH)
        {
            normalized = normalized[..EmailContentLimits.MAX_SUBJECT_LENGTH];
        }

        return normalized;
    }

    private static string WrapHtmlLayout(string heading, string bodyHtml)
    {
        var safeHeading = HtmlEncoder.Default.Encode(heading);
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>{safeHeading}</title></head>
            <body>
            <h1>{safeHeading}</h1>
            {bodyHtml}
            <p style="color:#666;font-size:12px;">AssetBlock transactional notice</p>
            </body>
            </html>
            """;
    }

    private static void EnsureBounded(string subject, string text, string html)
    {
        if (subject.Length > EmailContentLimits.MAX_SUBJECT_LENGTH)
        {
            throw new InvalidOperationException(
                $"Email subject exceeds {EmailContentLimits.MAX_SUBJECT_LENGTH} characters.");
        }

        if (text.Length > EmailContentLimits.MAX_BODY_LENGTH)
        {
            throw new InvalidOperationException(
                $"Email text body exceeds {EmailContentLimits.MAX_BODY_LENGTH} characters.");
        }

        if (html.Length > EmailContentLimits.MAX_BODY_LENGTH)
        {
            throw new InvalidOperationException(
                $"Email HTML body exceeds {EmailContentLimits.MAX_BODY_LENGTH} characters.");
        }
    }
}
