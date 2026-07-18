using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Infrastructure.Tests.Outbox;

public sealed class EmailDispatchOutboxHandlerTests
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IEmailSender _emailSender = Substitute.For<IEmailSender>();
    private readonly EmailDispatchOutboxHandler _sut;

    public EmailDispatchOutboxHandlerTests()
    {
        _sut = new EmailDispatchOutboxHandler(
            _emailSender,
            Microsoft.Extensions.Options.Options.Create(new EmailOptions
            {
                Provider = "Smtp",
                FromName = "AssetBlock",
                FromAddress = "noreply@localhost",
                PublicAppBaseUrl = "http://localhost:3000",
                MessageIdDomain = "mail.localhost",
                Smtp = new EmailSmtpOptions { Host = "localhost", Port = 1025, Security = SmtpSecurityMode.NONE, TimeoutSeconds = 30 }
            }),
            NullLogger<EmailDispatchOutboxHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenPayloadValid_ShouldDeriveStableMessageIdAndSendOnce()
    {
        var outboxId = Guid.NewGuid();
        var recipientUserId = Guid.NewGuid();
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            "Subject",
            "text",
            "<p>html</p>");
        var message = new OutboxMessage
        {
            Id = outboxId,
            Type = "EmailDispatch",
            Payload = JsonSerializer.Serialize(payload, _json),
            OccurredAt = DateTimeOffset.UtcNow
        };

        message.Payload.Should().Contain("\"templateKind\":\"PURCHASE_RECEIPT\"");
        message.Payload.Should().NotContain("\"templateKind\":0");

        await _sut.Handle(message, CancellationToken.None);

        await _emailSender.Received(1).Send(
            Arg.Is<EmailMessage>(m =>
                m.RecipientAddress == "buyer@example.com"
                && m.RecipientUserId == recipientUserId
                && m.TemplateKind == EmailTemplateKind.PURCHASE_RECEIPT
                && m.MessageId == EmailDispatchOutboxHandler.BuildMessageId(outboxId, "mail.localhost")
                && m.Subject == "Subject"
                && m.TextBody == "text"
                && m.HtmlBody == "<p>html</p>"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPayloadUsesStableTemplateString_ShouldDeserializeAndSend()
    {
        var outboxId = Guid.NewGuid();
        var recipientUserId = Guid.NewGuid();
        var payloadJson =
            $$"""
            {"recipientAddress":"buyer@example.com","recipientUserId":"{{recipientUserId}}","templateKind":"ASSET_SOLD","subject":"Asset sold","textBody":"text","htmlBody":"<p>html</p>"}
            """;
        var message = new OutboxMessage
        {
            Id = outboxId,
            Type = "EmailDispatch",
            Payload = payloadJson,
            OccurredAt = DateTimeOffset.UtcNow
        };

        await _sut.Handle(message, CancellationToken.None);

        await _emailSender.Received(1).Send(
            Arg.Is<EmailMessage>(m => m.TemplateKind == EmailTemplateKind.ASSET_SOLD),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPayloadMalformed_ShouldThrow()
    {
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "EmailDispatch",
            Payload = "{not-json",
            OccurredAt = DateTimeOffset.UtcNow
        };

        var act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenRecipientInvalid_ShouldNotCallSender()
    {
        var payload = new EmailDispatchPayload(
            "not-an-email",
            Guid.NewGuid(),
            EmailTemplateKind.PURCHASE_RECEIPT,
            "Subject",
            "text",
            "<p>html</p>");
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "EmailDispatch",
            Payload = JsonSerializer.Serialize(payload, _json),
            OccurredAt = DateTimeOffset.UtcNow
        };

        var act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*recipient address*");
        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderCancels_ShouldPropagateCancellation()
    {
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            Guid.NewGuid(),
            EmailTemplateKind.PURCHASE_RECEIPT,
            "Subject",
            "text",
            "<p>html</p>");
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "EmailDispatch",
            Payload = JsonSerializer.Serialize(payload, _json),
            OccurredAt = DateTimeOffset.UtcNow
        };
        _emailSender.Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Handle_WhenSenderFails_ShouldThrowSafeExceptionWithoutOriginalDetails()
    {
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            Guid.NewGuid(),
            EmailTemplateKind.PURCHASE_RECEIPT,
            "Subject",
            "text",
            "<p>html</p>");
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "EmailDispatch",
            Payload = JsonSerializer.Serialize(payload, _json),
            OccurredAt = DateTimeOffset.UtcNow
        };
        _emailSender.Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP rejected buyer@example.com with MIME body leak"));

        var act = () => _sut.Handle(message, CancellationToken.None);
        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Be("Email transport failed.");
        exception.Which.InnerException.Should().BeNull();
        exception.Which.Message.Should().NotContain("buyer@example.com");
        exception.Which.Message.Should().NotContain("MIME");
        exception.Which.Message.Should().NotContain("SMTP rejected");
    }
}
