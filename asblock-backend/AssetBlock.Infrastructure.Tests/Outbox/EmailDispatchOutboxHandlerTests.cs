using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Outbox;
using AwesomeAssertions.Specialized;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Infrastructure.Tests.Outbox;

public sealed class EmailDispatchOutboxHandlerTests
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IEmailSender _emailSender = Substitute.For<IEmailSender>();
    private readonly IEmailDeliveryStore _emailDeliveryStore = Substitute.For<IEmailDeliveryStore>();
    private readonly EmailDispatchOutboxHandler _sut;
    private readonly Guid _defaultClaimToken = Guid.NewGuid();

    public EmailDispatchOutboxHandlerTests()
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

        _sut = new EmailDispatchOutboxHandler(
            _emailSender,
            _emailDeliveryStore,
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
    public async Task Handle_WhenAlreadyDelivered_ShouldSkipSendingAndReturn()
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

        _emailDeliveryStore.TryClaimDelivery(
            outboxId,
            Arg.Any<string>(),
            "buyer@example.com",
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
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

        _emailDeliveryStore.TryClaimDelivery(
            outboxId,
            Arg.Any<string>(),
            "buyer@example.com",
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>())
            .Returns((DeliveryClaimStatus.CONCURRENT_CONFLICT, (Guid?)null));

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*locked by another worker*");
        await _emailSender.DidNotReceive().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPayloadValid_ShouldDeriveStableMessageIdAndSendOnceAndConfirm()
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

        await _emailDeliveryStore.Received(1).ConfirmDelivery(
            outboxId,
            _defaultClaimToken,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenConfirmDeliveryReturnsFalse_ShouldNotReleaseClaimAndShouldThrowClaimLostException()
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

        _emailDeliveryStore.ConfirmDelivery(
            outboxId,
            _defaultClaimToken,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Email delivery claim was lost before confirmation.");

        // Must NOT release claim because SMTP transport already succeeded
        await _emailDeliveryStore.DidNotReceive().ReleaseClaim(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderSucceedsAndConfirmationThrows_ShouldNotReleaseClaimAndShouldThrowSafeException()
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

        _emailDeliveryStore.ConfirmDelivery(
            outboxId,
            _defaultClaimToken,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB connection error during confirmation"));

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        ExceptionAssertions<InvalidOperationException> ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Be("Email transport failed.");

        // Must NOT release claim after successful SMTP send
        await _emailDeliveryStore.DidNotReceive().ReleaseClaim(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderSucceedsAndCallerTokenCancels_ShouldStillConfirmWithBoundedTokenAndSucceed()
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

        using var cts = new CancellationTokenSource();

        _emailSender.Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                // Cancellation arrives right after SMTP send finishes
                cts.Cancel();
                await Task.CompletedTask;
            });

        await _sut.Handle(message, cts.Token);

        await _emailDeliveryStore.Received(1).ConfirmDelivery(
            outboxId,
            _defaultClaimToken,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());

        await _emailDeliveryStore.DidNotReceive().ReleaseClaim(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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

        await _emailDeliveryStore.Received(1).ConfirmDelivery(
            outboxId,
            _defaultClaimToken,
            Arg.Any<DateTimeOffset>(),
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

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTemplateKindUndefined_ShouldThrowWithoutCallingSenderOrStore()
    {
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            Guid.NewGuid(),
            (EmailTemplateKind)999,
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

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*template kind*");
        await _emailDeliveryStore.DidNotReceiveWithAnyArgs().TryClaimDelivery(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<EmailTemplateKind>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSubjectExceedsMaxLength_ShouldThrowWithoutCallingSenderOrStore()
    {
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            Guid.NewGuid(),
            EmailTemplateKind.PURCHASE_RECEIPT,
            new string('A', EmailContentLimits.MAX_SUBJECT_LENGTH + 1),
            "text",
            "<p>html</p>");
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "EmailDispatch",
            Payload = JsonSerializer.Serialize(payload, _json),
            OccurredAt = DateTimeOffset.UtcNow
        };

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*subject*");
        await _emailDeliveryStore.DidNotReceiveWithAnyArgs().TryClaimDelivery(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<EmailTemplateKind>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTextBodyExceedsMaxLength_ShouldThrowWithoutCallingSenderOrStore()
    {
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            Guid.NewGuid(),
            EmailTemplateKind.PURCHASE_RECEIPT,
            "Subject",
            new string('A', EmailContentLimits.MAX_BODY_LENGTH + 1),
            "<p>html</p>");
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "EmailDispatch",
            Payload = JsonSerializer.Serialize(payload, _json),
            OccurredAt = DateTimeOffset.UtcNow
        };

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*text body*");
        await _emailDeliveryStore.DidNotReceiveWithAnyArgs().TryClaimDelivery(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<EmailTemplateKind>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenHtmlBodyExceedsMaxLength_ShouldThrowWithoutCallingSenderOrStore()
    {
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            Guid.NewGuid(),
            EmailTemplateKind.PURCHASE_RECEIPT,
            "Subject",
            "text",
            new string('A', EmailContentLimits.MAX_BODY_LENGTH + 1));
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "EmailDispatch",
            Payload = JsonSerializer.Serialize(payload, _json),
            OccurredAt = DateTimeOffset.UtcNow
        };

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*html body*");
        await _emailDeliveryStore.DidNotReceiveWithAnyArgs().TryClaimDelivery(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<EmailTemplateKind>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
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

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*recipient address*");
        await _emailSender.DidNotReceiveWithAnyArgs().Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderCancels_ShouldReleaseClaimAndPropagateCancellation()
    {
        var outboxId = Guid.NewGuid();
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            Guid.NewGuid(),
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

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _emailSender.Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Func<Task> act = () => _sut.Handle(message, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        await _emailDeliveryStore.Received(1).ReleaseClaim(outboxId, _defaultClaimToken, Arg.Any<CancellationToken>());
        await _emailDeliveryStore.DidNotReceive().ConfirmDelivery(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderCancelsAndReleaseClaimFails_ShouldPreserveOriginalCancellation()
    {
        var outboxId = Guid.NewGuid();
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            Guid.NewGuid(),
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

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _emailSender.Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        _emailDeliveryStore.ReleaseClaim(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error during release"));

        Func<Task> act = () => _sut.Handle(message, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Handle_WhenSenderFails_ShouldReleaseClaimAndThrowSafeException()
    {
        var outboxId = Guid.NewGuid();
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            Guid.NewGuid(),
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
        _emailSender.Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP rejected buyer@example.com with MIME body leak"));

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);
        ExceptionAssertions<InvalidOperationException> exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Be("Email transport failed.");
        exception.Which.InnerException.Should().BeNull();
        exception.Which.Message.Should().NotContain("buyer@example.com");
        exception.Which.Message.Should().NotContain("MIME");
        exception.Which.Message.Should().NotContain("SMTP rejected");

        await _emailDeliveryStore.Received(1).ReleaseClaim(outboxId, _defaultClaimToken, Arg.Any<CancellationToken>());
        await _emailDeliveryStore.DidNotReceive().ConfirmDelivery(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderFailsAndReleaseClaimAlsoFails_ShouldPreserveOriginalSafeException()
    {
        var outboxId = Guid.NewGuid();
        var payload = new EmailDispatchPayload(
            "buyer@example.com",
            Guid.NewGuid(),
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
        _emailSender.Send(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP crash"));
        _emailDeliveryStore.ReleaseClaim(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB connection closed"));

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);
        ExceptionAssertions<InvalidOperationException> exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Be("Email transport failed.");
    }
}
