using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Infrastructure.Tests.Outbox;

public sealed class EmailActionDispatchOutboxHandlerTests
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IEmailSender _emailSender = Substitute.For<IEmailSender>();
    private readonly IEmailDeliveryStore _emailDeliveryStore = Substitute.For<IEmailDeliveryStore>();
    private readonly IEmailActionStore _emailActionStore = Substitute.For<IEmailActionStore>();
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IEmailActionLinkProtector _linkProtector = Substitute.For<IEmailActionLinkProtector>();
    private readonly ITransactionalEmailComposer _emailComposer = Substitute.For<ITransactionalEmailComposer>();
    private readonly EmailActionDispatchOutboxHandler _sut;
    private readonly Guid _defaultClaimToken = Guid.NewGuid();

    private static readonly IOptions<EmailOptions> _emailOptions = Microsoft.Extensions.Options.Options.Create(new EmailOptions
    {
        Provider = "Smtp",
        FromName = "AssetBlock",
        FromAddress = "noreply@localhost",
        PublicAppBaseUrl = "http://localhost:3000",
        MessageIdDomain = "mail.localhost",
        Smtp = new EmailSmtpOptions { Host = "localhost", Port = 1025, Security = SmtpSecurityMode.NONE, TimeoutSeconds = 30 }
    });

    public EmailActionDispatchOutboxHandlerTests()
    {
        _emailDeliveryStore.TryClaimDelivery(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<EmailTemplateKind>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>())
            .Returns((DeliveryClaimStatus.CLAIMED, _defaultClaimToken));

        _emailDeliveryStore.ConfirmDelivery(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        _sut = new EmailActionDispatchOutboxHandler(
            _emailSender,
            _emailDeliveryStore,
            _emailActionStore,
            _userStore,
            _linkProtector,
            _emailComposer,
            _emailOptions,
            NullLogger<EmailActionDispatchOutboxHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAlreadyDelivered_ShouldSkipSendingAndReturn()
    {
        var (payload, message) = BuildValidPayload();
        var recipientEmail = "verify@example.test";
        var action = BuildAction(payload, targetEmail: recipientEmail);
        var recipient = new EmailRecipient(payload.RecipientUserId, recipientEmail);
        var composedEmail = new EmailMessage(recipientEmail, payload.RecipientUserId, "Subject", "text", "<p>html</p>", EmailTemplateKind.EMAIL_VERIFICATION, "msgid@mail.localhost");

        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetEmailRecipientById(payload.RecipientUserId, Arg.Any<CancellationToken>()).Returns(recipient);
        _linkProtector.Protect(Arg.Any<EmailActionLinkClaims>()).Returns("protected-token");
        _linkProtector.BuildActionUrl(EmailActionPurpose.EMAIL_VERIFICATION, "protected-token").Returns("http://localhost:3000/verify-email#token=protected-token");
        _emailComposer.CreateEmailVerification(recipientEmail, payload.RecipientUserId, Arg.Any<string>()).Returns(composedEmail);

        _emailDeliveryStore.TryClaimDelivery(
            message.Id,
            Arg.Any<string>(),
            recipientEmail,
            payload.RecipientUserId,
            EmailTemplateKind.EMAIL_VERIFICATION,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>())
            .Returns((DeliveryClaimStatus.ALREADY_DELIVERED, (Guid?)null));

        await _sut.Handle(message, CancellationToken.None);

        await _emailSender.DidNotReceive().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await _emailDeliveryStore.DidNotReceive().ConfirmDelivery(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenConcurrentConflict_ShouldThrowWithoutSending()
    {
        var (payload, message) = BuildValidPayload();
        var recipientEmail = "verify@example.test";
        var action = BuildAction(payload, targetEmail: recipientEmail);
        var recipient = new EmailRecipient(payload.RecipientUserId, recipientEmail);
        var composedEmail = new EmailMessage(recipientEmail, payload.RecipientUserId, "Subject", "text", "<p>html</p>", EmailTemplateKind.EMAIL_VERIFICATION, "msgid@mail.localhost");

        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetEmailRecipientById(payload.RecipientUserId, Arg.Any<CancellationToken>()).Returns(recipient);
        _linkProtector.Protect(Arg.Any<EmailActionLinkClaims>()).Returns("protected-token");
        _linkProtector.BuildActionUrl(EmailActionPurpose.EMAIL_VERIFICATION, "protected-token").Returns("http://localhost:3000/verify-email#token=protected-token");
        _emailComposer.CreateEmailVerification(recipientEmail, payload.RecipientUserId, Arg.Any<string>()).Returns(composedEmail);

        _emailDeliveryStore.TryClaimDelivery(
            message.Id,
            Arg.Any<string>(),
            recipientEmail,
            payload.RecipientUserId,
            EmailTemplateKind.EMAIL_VERIFICATION,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>())
            .Returns((DeliveryClaimStatus.CONCURRENT_CONFLICT, (Guid?)null));

        var act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*locked by another worker*");
        await _emailSender.DidNotReceive().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenConfirmDeliveryReturnsFalse_ShouldThrowClaimLostException()
    {
        var (payload, message) = BuildValidPayload();
        var recipientEmail = "verify@example.test";
        var action = BuildAction(payload, targetEmail: recipientEmail);
        var recipient = new EmailRecipient(payload.RecipientUserId, recipientEmail);
        var composedEmail = new EmailMessage(recipientEmail, payload.RecipientUserId, "Subject", "text", "<p>html</p>", EmailTemplateKind.EMAIL_VERIFICATION, "msgid@mail.localhost");

        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetEmailRecipientById(payload.RecipientUserId, Arg.Any<CancellationToken>()).Returns(recipient);
        _linkProtector.Protect(Arg.Any<EmailActionLinkClaims>()).Returns("protected-token");
        _linkProtector.BuildActionUrl(EmailActionPurpose.EMAIL_VERIFICATION, "protected-token").Returns("http://localhost:3000/verify-email#token=protected-token");
        _emailComposer.CreateEmailVerification(recipientEmail, payload.RecipientUserId, Arg.Any<string>()).Returns(composedEmail);

        _emailDeliveryStore.ConfirmDelivery(
            message.Id,
            _defaultClaimToken,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        var act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Email delivery claim was lost before confirmation.");

        // Must NOT release claim after successful SMTP send
        await _emailDeliveryStore.DidNotReceive().ReleaseClaim(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderSucceedsAndConfirmationThrows_ShouldNotReleaseClaimAndShouldThrowSafeException()
    {
        var (payload, message) = BuildValidPayload();
        var recipientEmail = "verify@example.test";
        var action = BuildAction(payload, targetEmail: recipientEmail);
        var recipient = new EmailRecipient(payload.RecipientUserId, recipientEmail);
        var composedEmail = new EmailMessage(recipientEmail, payload.RecipientUserId, "Subject", "text", "<p>html</p>", EmailTemplateKind.EMAIL_VERIFICATION, "msgid@mail.localhost");

        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetEmailRecipientById(payload.RecipientUserId, Arg.Any<CancellationToken>()).Returns(recipient);
        _linkProtector.Protect(Arg.Any<EmailActionLinkClaims>()).Returns("protected-token");
        _linkProtector.BuildActionUrl(EmailActionPurpose.EMAIL_VERIFICATION, "protected-token").Returns("http://localhost:3000/verify-email#token=protected-token");
        _emailComposer.CreateEmailVerification(recipientEmail, payload.RecipientUserId, Arg.Any<string>()).Returns(composedEmail);

        _emailDeliveryStore.ConfirmDelivery(
            message.Id,
            _defaultClaimToken,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error during confirm"));

        var act = () => _sut.Handle(message, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Be("Email transport failed.");

        // Must NOT release claim after successful SMTP send
        await _emailDeliveryStore.DidNotReceive().ReleaseClaim(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderSucceedsAndCallerTokenCancels_ShouldStillConfirmWithBoundedTokenAndSucceed()
    {
        var (payload, message) = BuildValidPayload();
        var recipientEmail = "verify@example.test";
        var action = BuildAction(payload, targetEmail: recipientEmail);
        var recipient = new EmailRecipient(payload.RecipientUserId, recipientEmail);
        var composedEmail = new EmailMessage(recipientEmail, payload.RecipientUserId, "Subject", "text", "<p>html</p>", EmailTemplateKind.EMAIL_VERIFICATION, "msgid@mail.localhost");

        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetEmailRecipientById(payload.RecipientUserId, Arg.Any<CancellationToken>()).Returns(recipient);
        _linkProtector.Protect(Arg.Any<EmailActionLinkClaims>()).Returns("protected-token");
        _linkProtector.BuildActionUrl(EmailActionPurpose.EMAIL_VERIFICATION, "protected-token").Returns("http://localhost:3000/verify-email#token=protected-token");
        _emailComposer.CreateEmailVerification(recipientEmail, payload.RecipientUserId, Arg.Any<string>()).Returns(composedEmail);

        using var cts = new CancellationTokenSource();
        _emailSender.Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await cts.CancelAsync();
                await Task.CompletedTask;
            });

        await _sut.Handle(message, cts.Token);

        await _emailDeliveryStore.Received(1).ConfirmDelivery(
            message.Id,
            _defaultClaimToken,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());

        await _emailDeliveryStore.DidNotReceive().ReleaseClaim(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenValid_ShouldSendAndConfirmDelivery()
    {
        var (payload, message) = BuildValidPayload();
        var recipientEmail = "verify@example.test";
        var action = BuildAction(payload, targetEmail: recipientEmail);
        var recipient = new EmailRecipient(payload.RecipientUserId, recipientEmail);
        var composedEmail = new EmailMessage(recipientEmail, payload.RecipientUserId, "Subject", "text", "<p>html</p>", EmailTemplateKind.EMAIL_VERIFICATION, "msgid@mail.localhost");

        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetEmailRecipientById(payload.RecipientUserId, Arg.Any<CancellationToken>()).Returns(recipient);
        _linkProtector.Protect(Arg.Any<EmailActionLinkClaims>()).Returns("protected-token");
        _linkProtector.BuildActionUrl(EmailActionPurpose.EMAIL_VERIFICATION, "protected-token").Returns("http://localhost:3000/verify-email#token=protected-token");
        _emailComposer.CreateEmailVerification(recipientEmail, payload.RecipientUserId, Arg.Any<string>()).Returns(composedEmail);

        await _sut.Handle(message, CancellationToken.None);

        await _emailSender.Received(1).Send(
            Arg.Is<EmailMessage>(m =>
                m.RecipientAddress == recipientEmail
                && m.RecipientUserId == payload.RecipientUserId
                && m.TemplateKind == EmailTemplateKind.EMAIL_VERIFICATION
                && m.MessageId == $"<{message.Id:N}@mail.localhost>"),
            Arg.Any<CancellationToken>());
        await _emailDeliveryStore.Received(1).ConfirmDelivery(
            message.Id,
            _defaultClaimToken,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenActionVersionMismatch_ShouldSkipWithoutSending()
    {
        var (payload, message) = BuildValidPayload();
        var action = BuildAction(payload, targetEmail: "verify@example.test");
        action.Version = Guid.NewGuid();

        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);

        await _sut.Handle(message, CancellationToken.None);

        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderFails_ShouldReleaseClaimAndThrowSafeException()
    {
        var (payload, message) = BuildValidPayload();
        var recipientEmail = "verify@example.test";
        var action = BuildAction(payload, targetEmail: recipientEmail);
        var recipient = new EmailRecipient(payload.RecipientUserId, recipientEmail);
        var composedEmail = new EmailMessage(recipientEmail, payload.RecipientUserId, "Subject", "text", "<p>html</p>", EmailTemplateKind.EMAIL_VERIFICATION, "msgid@mail.localhost");

        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetEmailRecipientById(payload.RecipientUserId, Arg.Any<CancellationToken>()).Returns(recipient);
        _linkProtector.Protect(Arg.Any<EmailActionLinkClaims>()).Returns("protected-token");
        _linkProtector.BuildActionUrl(EmailActionPurpose.EMAIL_VERIFICATION, "protected-token").Returns("http://localhost:3000/verify-email#token=protected-token");
        _emailComposer.CreateEmailVerification(recipientEmail, payload.RecipientUserId, Arg.Any<string>()).Returns(composedEmail);
        _emailSender.Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP inner detail that must not leak"));

        var act = () => _sut.Handle(message, CancellationToken.None);
        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Be("Email transport failed.");
        exception.Which.Message.Should().NotContain("SMTP inner detail");

        await _emailDeliveryStore.Received(1).ReleaseClaim(message.Id, _defaultClaimToken, Arg.Any<CancellationToken>());
        await _emailDeliveryStore.DidNotReceive().ConfirmDelivery(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderFailsAndReleaseClaimAlsoFails_ShouldPreserveOriginalSafeException()
    {
        var (payload, message) = BuildValidPayload();
        var recipientEmail = "verify@example.test";
        var action = BuildAction(payload, targetEmail: recipientEmail);
        var recipient = new EmailRecipient(payload.RecipientUserId, recipientEmail);
        var composedEmail = new EmailMessage(recipientEmail, payload.RecipientUserId, "Subject", "text", "<p>html</p>", EmailTemplateKind.EMAIL_VERIFICATION, "msgid@mail.localhost");

        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetEmailRecipientById(payload.RecipientUserId, Arg.Any<CancellationToken>()).Returns(recipient);
        _linkProtector.Protect(Arg.Any<EmailActionLinkClaims>()).Returns("protected-token");
        _linkProtector.BuildActionUrl(EmailActionPurpose.EMAIL_VERIFICATION, "protected-token").Returns("http://localhost:3000/verify-email#token=protected-token");
        _emailComposer.CreateEmailVerification(recipientEmail, payload.RecipientUserId, Arg.Any<string>()).Returns(composedEmail);
        _emailSender.Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP inner detail"));
        _emailDeliveryStore.ReleaseClaim(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB fail"));

        var act = () => _sut.Handle(message, CancellationToken.None);
        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Be("Email transport failed.");
    }

    [Fact]
    public void Payload_Serialized_ShouldNotContainSensitiveFields()
    {
        var payload = new EmailActionDispatchPayload(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            EmailTemplateKind.EMAIL_VERIFICATION);

        var json = JsonSerializer.Serialize(payload, _json);
        var jsonLower = json.ToLowerInvariant();

        jsonLower.Should().NotContain("\"token\"");
        jsonLower.Should().NotContain("password");
        jsonLower.Should().NotContain("body");
        jsonLower.Should().NotContain("url");
        json.Should().Contain("emailActionId");
        json.Should().Contain("actionVersion");
        json.Should().Contain("recipientUserId");
        json.Should().Contain("templateKind");
    }

    private static (EmailActionDispatchPayload Payload, OutboxMessage Message) BuildValidPayload(
        EmailTemplateKind kind = EmailTemplateKind.EMAIL_VERIFICATION)
    {
        var outboxId = Guid.NewGuid();
        var payload = new EmailActionDispatchPayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind);
        var message = BuildMessage(JsonSerializer.Serialize(payload, _json), outboxId);
        return (payload, message);
    }

    private static OutboxMessage BuildMessage(string payloadJson, Guid id) => new()
    {
        Id = id,
        Type = OutboxMessageTypes.EMAIL_ACTION_DISPATCH,
        Payload = payloadJson,
        OccurredAt = DateTimeOffset.UtcNow
    };

    private static EmailAction BuildAction(
        EmailActionDispatchPayload payload,
        bool consumed = false,
        DateTimeOffset? expiresAt = null,
        string targetEmail = "test@example.test") => new()
    {
        Id = payload.EmailActionId,
        UserId = payload.RecipientUserId,
        Purpose = EmailActionPurpose.EMAIL_VERIFICATION,
        TargetEmail = targetEmail,
        Version = payload.ActionVersion,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(24),
        ConsumedAt = consumed ? DateTimeOffset.UtcNow : null
    };
}
