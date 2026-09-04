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

internal sealed class EmailActionDispatchOutboxHandler(
    IEmailSender emailSender,
    IEmailDeliveryStore emailDeliveryStore,
    IEmailActionStore emailActionStore,
    IUserStore userStore,
    IEmailActionLinkProtector linkProtector,
    ITransactionalEmailComposer emailComposer,
    IOptions<EmailOptions> emailOptions,
    ILogger<EmailActionDispatchOutboxHandler> logger,
    TimeProvider? timeProvider = null) : IOutboxMessageHandler
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private const string SAFE_TRANSPORT_FAILURE = "Email transport failed.";
    private const string CLAIM_LOST_FAILURE = "Email delivery claim was lost before confirmation.";

    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly HashSet<EmailTemplateKind> _allowedKinds =
    [
        EmailTemplateKind.EMAIL_VERIFICATION,
        EmailTemplateKind.PASSWORD_RESET,
        EmailTemplateKind.EMAIL_CHANGE_CONFIRMATION
    ];

    public string MessageType => OutboxMessageTypes.EMAIL_ACTION_DISPATCH;

    public async Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        EmailActionDispatchPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<EmailActionDispatchPayload>(message.Payload, _json)
                ?? throw new InvalidOperationException("EmailActionDispatch payload deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid EmailActionDispatch payload JSON.", ex);
        }

        ValidatePayload(payload);

        EmailAction? action = await emailActionStore.GetById(payload.EmailActionId, cancellationToken);
        if (action is null
            || action.UserId != payload.RecipientUserId
            || action.Version != payload.ActionVersion
            || action.ConsumedAt is not null
            || action.ExpiresAt <= _timeProvider.GetUtcNow()
            || !MatchesPurpose(payload.TemplateKind, action.Purpose))
        {
            AssetBlockDiagnostics.RecordEmailActionDispatchSkipped(EmailActionSkipReasons.STALE_OR_INVALID_ACTION);
            logger.LogInformation(
                "EmailActionDispatch skipped stale action: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId);
            return;
        }

        EmailRecipient? recipient = await userStore.GetEmailRecipientById(payload.RecipientUserId, cancellationToken);
        if (recipient is null
            || !string.Equals(recipient.Email, action.TargetEmail, StringComparison.OrdinalIgnoreCase))
        {
            // EMAIL_CHANGE targets new mailbox before User.Email updates; still send to TargetEmail.
            if (action.Purpose != EmailActionPurpose.EMAIL_CHANGE
                || string.IsNullOrWhiteSpace(action.TargetEmail))
            {
                AssetBlockDiagnostics.RecordEmailActionDispatchSkipped(EmailActionSkipReasons.UNAVAILABLE_RECIPIENT);
                logger.LogInformation(
                    "EmailActionDispatch skipped missing recipient: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
                    message.Id,
                    payload.TemplateKind,
                    payload.RecipientUserId);
                return;
            }
        }

        var deliveryAddress = action.Purpose == EmailActionPurpose.EMAIL_CHANGE
            ? action.TargetEmail
            : recipient!.Email;

        if (!TryValidateMailbox(deliveryAddress))
        {
            throw new InvalidOperationException("EmailActionDispatch recipient address is invalid.");
        }

        var claims = new EmailActionLinkClaims(action.Id, action.Version, action.Purpose, action.ExpiresAt);
        string actionUrl;
        try
        {
            var protectedToken = linkProtector.Protect(claims);
            actionUrl = linkProtector.BuildActionUrl(action.Purpose, protectedToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "EmailActionDispatch protect failed: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}, ExceptionType {ExceptionType}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId,
                ex.GetType().FullName);
            throw new InvalidOperationException(SAFE_TRANSPORT_FAILURE);
        }

        var smtpTimeoutSeconds = Math.Max(emailOptions.Value.Smtp.TimeoutSeconds, 5);
        var sendDeadline = TimeSpan.FromSeconds(smtpTimeoutSeconds);
        var claimSafetyMargin = TimeSpan.FromSeconds(Math.Max(smtpTimeoutSeconds, 30));
        TimeSpan claimDuration = sendDeadline + claimSafetyMargin;

        EmailMessage email = CreateMessage(payload.TemplateKind, deliveryAddress, payload.RecipientUserId, actionUrl, message.Id);

        (DeliveryClaimStatus claimStatus, Guid? claimToken) = await emailDeliveryStore.TryClaimDelivery(
            message.Id,
            email.MessageId,
            deliveryAddress,
            payload.RecipientUserId,
            payload.TemplateKind,
            claimDuration,
            cancellationToken);

        if (claimStatus == DeliveryClaimStatus.ALREADY_DELIVERED)
        {
            logger.LogInformation(
                "EmailActionDispatch already delivered: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId);
            return;
        }

        if (claimStatus == DeliveryClaimStatus.CONCURRENT_CONFLICT)
        {
            logger.LogWarning(
                "EmailActionDispatch active claim exists: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId);
            throw new InvalidOperationException("Email delivery is currently locked by another worker.");
        }

        logger.LogInformation(
            "EmailActionDispatch starting: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
            message.Id,
            payload.TemplateKind,
            payload.RecipientUserId);

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
                _timeProvider.GetUtcNow(),
                confirmCts.Token);

            if (!deliveryConfirmed)
            {
                logger.LogWarning(
                    "EmailActionDispatch confirmation failed (claim lost or expired): Outbox {OutboxId}",
                    message.Id);
                throw new InvalidOperationException(CLAIM_LOST_FAILURE);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !transportSucceeded)
        {
            throw;
        }
        catch (OperationCanceledException) when (sendCts.IsCancellationRequested && !transportSucceeded)
        {
            logger.LogWarning(
                "EmailActionDispatch send deadline exceeded: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId);
            throw new InvalidOperationException(SAFE_TRANSPORT_FAILURE);
        }
        catch (Exception ex) when (ex is not InvalidOperationException { Message: CLAIM_LOST_FAILURE })
        {
            logger.LogWarning(
                "EmailActionDispatch failed: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}, ExceptionType {ExceptionType}",
                message.Id,
                payload.TemplateKind,
                payload.RecipientUserId,
                ex.GetType().FullName);
            throw new InvalidOperationException(SAFE_TRANSPORT_FAILURE);
        }
        finally
        {
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
                        "EmailActionDispatch release claim failed: Outbox {OutboxId}, ExceptionType {ExceptionType}",
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
            "EmailActionDispatch succeeded: Outbox {OutboxId}, Template {TemplateKind}, RecipientUser {RecipientUserId}",
            message.Id,
            payload.TemplateKind,
            payload.RecipientUserId);
    }

    private EmailMessage CreateMessage(
        EmailTemplateKind kind,
        string recipientAddress,
        Guid recipientUserId,
        string actionUrl,
        Guid outboxId)
    {
        EmailMessage composed = kind switch
        {
            EmailTemplateKind.EMAIL_VERIFICATION =>
                emailComposer.CreateEmailVerification(recipientAddress, recipientUserId, actionUrl),
            EmailTemplateKind.PASSWORD_RESET =>
                emailComposer.CreatePasswordReset(recipientAddress, recipientUserId, actionUrl),
            EmailTemplateKind.EMAIL_CHANGE_CONFIRMATION =>
                emailComposer.CreateEmailChangeConfirmation(recipientAddress, recipientUserId, actionUrl),
            _ => throw new InvalidOperationException("EmailActionDispatch template kind is not allowed.")
        };

        return composed with
        {
            MessageId = $"<{outboxId:N}@{emailOptions.Value.MessageIdDomain.Trim()}>"
        };
    }

    private static void ValidatePayload(EmailActionDispatchPayload payload)
    {
        if (payload.EmailActionId == Guid.Empty
            || payload.ActionVersion == Guid.Empty
            || payload.RecipientUserId == Guid.Empty)
        {
            throw new InvalidOperationException("EmailActionDispatch payload identifiers are required.");
        }

        if (!_allowedKinds.Contains(payload.TemplateKind))
        {
            throw new InvalidOperationException("EmailActionDispatch payload template kind is not allowed.");
        }
    }

    private static bool MatchesPurpose(EmailTemplateKind kind, EmailActionPurpose purpose) =>
        (kind, purpose) switch
        {
            (EmailTemplateKind.EMAIL_VERIFICATION, EmailActionPurpose.EMAIL_VERIFICATION) => true,
            (EmailTemplateKind.PASSWORD_RESET, EmailActionPurpose.PASSWORD_RESET) => true,
            (EmailTemplateKind.EMAIL_CHANGE_CONFIRMATION, EmailActionPurpose.EMAIL_CHANGE) => true,
            _ => false
        };

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
