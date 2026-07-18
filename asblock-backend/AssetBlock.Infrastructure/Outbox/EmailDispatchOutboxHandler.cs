using System.Net.Mail;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed class EmailDispatchOutboxHandler(
    IEmailSender emailSender,
    IOptions<EmailOptions> emailOptions,
    ILogger<EmailDispatchOutboxHandler> logger) : IOutboxMessageHandler
{
    private const string SAFE_TRANSPORT_FAILURE = "Email transport failed.";

    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string MessageType => OutboxMessageTypes.EMAIL_DISPATCH;

    public async Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        EmailDispatchPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<EmailDispatchPayload>(message.Payload, _json)
                ?? throw new InvalidOperationException("EmailDispatch payload deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid EmailDispatch payload JSON.", ex);
        }

        ValidatePayload(payload);

        var messageId = BuildMessageId(message.Id, emailOptions.Value.MessageIdDomain);
        var email = new EmailMessage(
            payload.RecipientAddress.Trim(),
            payload.RecipientUserId,
            payload.Subject,
            payload.TextBody,
            payload.HtmlBody,
            payload.TemplateKind,
            messageId);

        logger.LogInformation(
            "EmailDispatch starting: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
            message.Id,
            payload.TemplateKind,
            payload.RecipientUserId);

        try
        {
            await emailSender.Send(email, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "EmailDispatch failed: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}, ExceptionType {ExceptionType}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId,
                ex.GetType().FullName);
            throw new InvalidOperationException(SAFE_TRANSPORT_FAILURE);
        }

        logger.LogInformation(
            "EmailDispatch succeeded: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
            message.Id,
            payload.TemplateKind,
            payload.RecipientUserId);
    }

    internal static string BuildMessageId(Guid outboxMessageId, string messageIdDomain)
    {
        var domain = messageIdDomain.Trim();
        return $"<{outboxMessageId:N}@{domain}>";
    }

    private static void ValidatePayload(EmailDispatchPayload payload)
    {
        if (payload.RecipientUserId == Guid.Empty)
        {
            throw new InvalidOperationException("EmailDispatch payload recipient user id is required.");
        }

        if (string.IsNullOrWhiteSpace(payload.RecipientAddress)
            || !TryValidateMailbox(payload.RecipientAddress))
        {
            throw new InvalidOperationException("EmailDispatch payload recipient address is invalid.");
        }

        if (!Enum.IsDefined(payload.TemplateKind))
        {
            throw new InvalidOperationException("EmailDispatch payload template kind is not allowed.");
        }

        if (string.IsNullOrWhiteSpace(payload.Subject)
            || payload.Subject.Length > EmailContentLimits.MAX_SUBJECT_LENGTH)
        {
            throw new InvalidOperationException("EmailDispatch payload subject is invalid.");
        }

        if (string.IsNullOrWhiteSpace(payload.TextBody)
            || payload.TextBody.Length > EmailContentLimits.MAX_BODY_LENGTH)
        {
            throw new InvalidOperationException("EmailDispatch payload text body is invalid.");
        }

        if (string.IsNullOrWhiteSpace(payload.HtmlBody)
            || payload.HtmlBody.Length > EmailContentLimits.MAX_BODY_LENGTH)
        {
            throw new InvalidOperationException("EmailDispatch payload HTML body is invalid.");
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
}
