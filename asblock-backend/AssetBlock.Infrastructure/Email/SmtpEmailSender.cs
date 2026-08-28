using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AssetBlock.Infrastructure.Email;

/// <summary>SMTP transport via MailKit. Works with Mailpit locally and SMTP relays in deployment.</summary>
internal sealed class SmtpEmailSender(
    IOptions<EmailOptions> emailOptions,
    ILogger<SmtpEmailSender>? logger = null,
    Func<ISmtpClient>? clientFactory = null) : IEmailSender
{
    private readonly EmailOptions _options = emailOptions.Value;
    private readonly ILogger<SmtpEmailSender> _logger = logger ?? NullLogger<SmtpEmailSender>.Instance;
    private readonly Func<ISmtpClient> _clientFactory = clientFactory ?? (() => new SmtpClient());

    public async Task Send(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mime = BuildMimeMessage(message);
        var smtp = _options.Smtp;
        var timeout = TimeSpan.FromSeconds(smtp.TimeoutSeconds);

        using var client = _clientFactory();
        client.Timeout = (int)timeout.TotalMilliseconds;
        var sendCompleted = false;

        try
        {
            await client.ConnectAsync(
                smtp.Host,
                smtp.Port,
                MapSecurity(smtp.Security),
                cancellationToken);

            var username = smtp.Username?.Trim() ?? string.Empty;
            var password = smtp.Password ?? string.Empty;
            if (username.Length > 0)
            {
                await client.AuthenticateAsync(username, password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            sendCompleted = true;
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    // Disconnect is best-effort with an independent bounded token.
                    // A failure during disconnect must not alter the outcome of a successfully completed send.
                    using var disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await client.DisconnectAsync(quit: true, disconnectCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "SMTP disconnect encountered an error (sendCompleted={SendCompleted}): ExceptionType {ExceptionType}",
                        sendCompleted,
                        ex.GetType().FullName);

                    // If send was not completed, do nothing here so the original exception from the try block propagates.
                    // If send was completed, suppress the disconnect error so transport is reported as successful.
                }
            }
        }
    }

    internal MimeMessage BuildMimeMessage(EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.MessageId = NormalizeMessageId(message.MessageId);
        mime.From.Add(new MailboxAddress(_options.FromName.Trim(), _options.FromAddress.Trim()));
        mime.To.Add(MailboxAddress.Parse(message.RecipientAddress.Trim()));
        mime.Subject = NormalizeSubject(message.Subject);

        var builder = new BodyBuilder
        {
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody
        };
        mime.Body = builder.ToMessageBody();
        return mime;
    }

    private static SecureSocketOptions MapSecurity(SmtpSecurityMode security) =>
        security switch
        {
            SmtpSecurityMode.NONE => SecureSocketOptions.None,
            SmtpSecurityMode.START_TLS => SecureSocketOptions.StartTls,
            SmtpSecurityMode.SSL_ON_CONNECT => SecureSocketOptions.SslOnConnect,
            _ => throw new InvalidOperationException($"Unsupported SMTP security mode: {security}.")
        };

    private static string NormalizeSubject(string subject) =>
        subject
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

    private static string NormalizeMessageId(string messageId)
    {
        var trimmed = messageId.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Message id is required.", nameof(messageId));
        }

        if (trimmed[0] == '<' && trimmed[^1] == '>')
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}
