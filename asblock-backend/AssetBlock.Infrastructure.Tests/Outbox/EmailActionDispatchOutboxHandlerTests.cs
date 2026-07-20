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
    private readonly IEmailActionStore _emailActionStore = Substitute.For<IEmailActionStore>();
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IEmailActionLinkProtector _linkProtector = Substitute.For<IEmailActionLinkProtector>();
    private readonly ITransactionalEmailComposer _emailComposer = Substitute.For<ITransactionalEmailComposer>();
    private readonly EmailActionDispatchOutboxHandler _sut;

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
        _sut = new EmailActionDispatchOutboxHandler(
            _emailSender,
            _emailActionStore,
            _userStore,
            _linkProtector,
            _emailComposer,
            _emailOptions,
            NullLogger<EmailActionDispatchOutboxHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenPayloadMalformed_ShouldThrow()
    {
        var message = BuildMessage("{bad-json", Guid.NewGuid());

        var act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenActionNotFound_ShouldSkipWithoutSending()
    {
        var (payload, message) = BuildValidPayload();
        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns((EmailAction?)null);

        await _sut.Handle(message, CancellationToken.None);

        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenActionConsumed_ShouldSkipWithoutSending()
    {
        var (payload, message) = BuildValidPayload();
        var action = BuildAction(payload, consumed: true);
        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);

        await _sut.Handle(message, CancellationToken.None);

        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenActionExpired_ShouldSkipWithoutSending()
    {
        var (payload, message) = BuildValidPayload();
        var action = BuildAction(payload, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));
        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);

        await _sut.Handle(message, CancellationToken.None);

        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenValidVerificationAction_ShouldProtectTokenAndSendEmail()
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

        await _emailSender.Received(1).Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        _linkProtector.Received(1).Protect(Arg.Is<EmailActionLinkClaims>(c =>
            c.ActionId == action.Id && c.Purpose == EmailActionPurpose.EMAIL_VERIFICATION));
    }

    [Fact]
    public async Task Handle_WhenActionVersionMismatch_ShouldSkipWithoutSending()
    {
        var (payload, message) = BuildValidPayload();
        var action = BuildAction(payload, targetEmail: "verify@example.test");
        action.Version = Guid.NewGuid(); // rotated after enqueue

        _emailActionStore.GetById(payload.EmailActionId, Arg.Any<CancellationToken>()).Returns(action);

        await _sut.Handle(message, CancellationToken.None);

        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderFails_ShouldThrowSafeException()
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
