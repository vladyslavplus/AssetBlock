using System.Diagnostics;
using System.Net.Mail;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed class EmailDispatchOutboxHandler(
    IEmailSender emailSender,
    IEmailDeliveryStore emailDeliveryStore,
    IOptions<EmailOptions> emailOptions,
    ILogger<EmailDispatchOutboxHandler> logger) : IOutboxMessageHandler
{
    private const string SAFE_TRANSPORT_FAILURE = "Email transport failed.";
    private const string CLAIM_LOST_FAILURE = "Email delivery claim was lost before confirmation.";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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

        var smtpTimeoutSeconds = Math.Max(emailOptions.Value.Smtp?.TimeoutSeconds ?? 30, 5);
        var sendDeadline = TimeSpan.FromSeconds(smtpTimeoutSeconds);
        var claimSafetyMargin = TimeSpan.FromSeconds(Math.Max(smtpTimeoutSeconds, 30));
        var claimDuration = sendDeadline + claimSafetyMargin;

        var messageId = BuildMessageId(message.Id, emailOptions.Value.MessageIdDomain);
        var (claimStatus, claimToken) = await emailDeliveryStore.TryClaimDelivery(
            message.Id,
            messageId,
            payload.RecipientAddress.Trim(),
            payload.RecipientUserId,
            payload.TemplateKind,
            claimDuration,
            cancellationToken);

        if (claimStatus == DeliveryClaimStatus.ALREADY_DELIVERED)
        {
            logger.LogInformation(
                "EmailDispatch already delivered: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId);
            return;
        }

        if (claimStatus == DeliveryClaimStatus.CONCURRENT_CONFLICT)
        {
            logger.LogWarning(
                "EmailDispatch active claim exists: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId);
            throw new InvalidOperationException("Email delivery is currently locked by another worker.");
        }

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

        var stopwatch = Stopwatch.StartNew();
        var outcome = DiagnosticsOutcome.SUCCESS;
        var transportSucceeded = false;
        bool deliveryConfirmed;

        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendCts.CancelAfter(sendDeadline);

        try
        {
            await emailSender.Send(email, sendCts.Token);
            transportSucceeded = true;

            using var confirmCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            deliveryConfirmed = await emailDeliveryStore.ConfirmDelivery(
                message.Id,
                claimToken!.Value,
                DateTimeOffset.UtcNow,
                confirmCts.Token);

            if (!deliveryConfirmed)
            {
                logger.LogWarning(
                    "EmailDispatch confirmation failed (claim lost or expired): Outbox {OutboxId}",
                    message.Id);
                outcome = DiagnosticsOutcome.FAILURE;
                throw new InvalidOperationException(CLAIM_LOST_FAILURE);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !transportSucceeded)
        {
            outcome = DiagnosticsOutcome.CANCELLED;
            throw;
        }
        catch (OperationCanceledException) when (sendCts.IsCancellationRequested && !transportSucceeded)
        {
            outcome = DiagnosticsOutcome.FAILURE;
            logger.LogWarning(
                "EmailDispatch send deadline exceeded: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId);
            throw new InvalidOperationException(SAFE_TRANSPORT_FAILURE);
        }
        catch (Exception ex) when (ex is not InvalidOperationException { Message: CLAIM_LOST_FAILURE })
        {
            outcome = DiagnosticsOutcome.FAILURE;
            logger.LogWarning(
                "EmailDispatch failed: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}, ExceptionType {ExceptionType}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId,
                ex.GetType().FullName);
            throw new InvalidOperationException(SAFE_TRANSPORT_FAILURE);
        }
        finally
        {
            AssetBlockDiagnostics.RecordEmailDispatch(stopwatch.Elapsed, payload.TemplateKind, outcome);
            if (!transportSucceeded && claimToken.HasValue)
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await emailDeliveryStore.ReleaseClaim(message.Id, claimToken.Value, cleanupCts.Token);
                }
                catch (Exception releaseEx)
                {
                    logger.LogWarning(
                        "EmailDispatch release claim failed: Outbox {OutboxId}, ExceptionType {ExceptionType}",
                        message.Id,
                        releaseEx.GetType().FullName);
                }
            }
        }

        if (!deliveryConfirmed)
        {
            throw new InvalidOperationException("Email delivery was not confirmed.");
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
        if (!Enum.IsDefined(payload.TemplateKind))
        {
            throw new InvalidOperationException("EmailDispatch payload template kind is invalid.");
        }

        if (payload.RecipientUserId == Guid.Empty)
        {
            throw new InvalidOperationException("EmailDispatch payload recipient user id is required.");
        }

        if (string.IsNullOrWhiteSpace(payload.RecipientAddress) || !TryValidateMailbox(payload.RecipientAddress))
        {
            throw new InvalidOperationException("EmailDispatch payload recipient address is invalid.");
        }

        if (string.IsNullOrWhiteSpace(payload.Subject) || payload.Subject.Length > EmailContentLimits.MAX_SUBJECT_LENGTH)
        {
            throw new InvalidOperationException("EmailDispatch payload subject is invalid or exceeds maximum length.");
        }

        if (string.IsNullOrWhiteSpace(payload.TextBody) || payload.TextBody.Length > EmailContentLimits.MAX_BODY_LENGTH)
        {
            throw new InvalidOperationException("EmailDispatch payload text body is invalid or exceeds maximum length.");
        }

        if (string.IsNullOrWhiteSpace(payload.HtmlBody) || payload.HtmlBody.Length > EmailContentLimits.MAX_BODY_LENGTH)
        {
            throw new InvalidOperationException("EmailDispatch payload html body is invalid or exceeds maximum length.");
        }
    }

    private static bool TryValidateMailbox(string address)
    {
        try
        {
            var parsed = new MailAddress(address.Trim());
            return !string.IsNullOrWhiteSpace(parsed.Address);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
