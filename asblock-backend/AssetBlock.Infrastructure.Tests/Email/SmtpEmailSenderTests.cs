using System.Net.Sockets;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Email;
using AwesomeAssertions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Infrastructure.Tests.Email;

public sealed class SmtpEmailSenderTests
{
    private readonly ISmtpClient _mockSmtpClient = Substitute.For<ISmtpClient>();
    private readonly TestLogger<SmtpEmailSender> _logger = new();

    private static IOptions<EmailOptions> CreateOptions(int timeoutSeconds = 30) =>
        Microsoft.Extensions.Options.Options.Create(new EmailOptions
        {
            Provider = "Smtp",
            FromName = "AssetBlock",
            FromAddress = "noreply@localhost",
            PublicAppBaseUrl = "http://localhost:3000",
            MessageIdDomain = "mail.localhost",
            Smtp = new EmailSmtpOptions
            {
                Host = "localhost",
                Port = 1025,
                Security = SmtpSecurityMode.NONE,
                TimeoutSeconds = timeoutSeconds
            }
        });

    private static EmailMessage CreateSampleMessage()
    {
        var outboxId = Guid.NewGuid();
        return new EmailMessage(
            "buyer@example.com",
            Guid.NewGuid(),
            "Purchase receipt: Pack",
            "Thanks for your purchase.",
            "<p>Thanks for your purchase.</p>",
            EmailTemplateKind.PURCHASE_RECEIPT,
            $"<{outboxId:N}@mail.localhost>");
    }

    [Fact]
    public void BuildMimeMessage_WhenValid_ShouldSetFromToSubjectMultipartAndMessageId()
    {
        var sut = new SmtpEmailSender(CreateOptions());
        var outboxId = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var message = new EmailMessage(
            "buyer@example.com",
            Guid.NewGuid(),
            "Hello\r\nWorld",
            "plain text",
            "<p>html</p>",
            EmailTemplateKind.PURCHASE_RECEIPT,
            $"<{outboxId:N}@mail.localhost>");

        var mime = sut.BuildMimeMessage(message);

        mime.From.Mailboxes.Single().Address.Should().Be("noreply@localhost");
        mime.To.Mailboxes.Single().Address.Should().Be("buyer@example.com");
        mime.Subject.Should().Be("HelloWorld");
        mime.MessageId.Should().Be($"{outboxId:N}@mail.localhost");
        mime.Body.Should().BeAssignableTo<MultipartAlternative>();
        mime.HtmlBody.Should().Contain("<p>html</p>");
        mime.TextBody.Should().Be("plain text");
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(30, 30000)]
    [InlineData(120, 120000)]
    public async Task Send_WhenTimeoutConfigured_ShouldSetSmtpClientTimeoutExactMilliseconds(int configuredTimeoutSeconds, int expectedMs)
    {
        var sut = new SmtpEmailSender(
            CreateOptions(configuredTimeoutSeconds),
            _logger,
            () => _mockSmtpClient);

        await sut.Send(CreateSampleMessage(), CancellationToken.None);

        _mockSmtpClient.Timeout.Should().Be(expectedMs);
    }

    [Fact]
    public async Task Send_WhenSendSucceedsAndDisconnectThrows_ShouldReturnSuccessfullyAndSuppressDisconnectException()
    {
        _mockSmtpClient.IsConnected.Returns(true);
        _mockSmtpClient.DisconnectAsync(true, Arg.Any<CancellationToken>())
            .ThrowsAsync(new SocketException((int)SocketError.ConnectionReset));

        var sut = new SmtpEmailSender(
            CreateOptions(),
            _logger,
            () => _mockSmtpClient);

        // Must NOT throw SocketException
        await sut.Send(CreateSampleMessage(), CancellationToken.None);

        await _mockSmtpClient.Received(1).SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>());
        await _mockSmtpClient.Received(1).DisconnectAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_WhenSendSucceedsAndDisconnectTimesOutOrCancels_ShouldReturnSuccessfully()
    {
        _mockSmtpClient.IsConnected.Returns(true);
        _mockSmtpClient.DisconnectAsync(true, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException("Disconnect timeout exceeded"));

        var sut = new SmtpEmailSender(
            CreateOptions(),
            _logger,
            () => _mockSmtpClient);

        await sut.Send(CreateSampleMessage(), CancellationToken.None);

        await _mockSmtpClient.Received(1).SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_WhenSendFailsAndDisconnectAlsoFails_ShouldThrowPrimarySendException()
    {
        var primaryException = new InvalidOperationException("SMTP 550 Mailbox unavailable");
        _mockSmtpClient.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(primaryException);
        _mockSmtpClient.IsConnected.Returns(true);
        _mockSmtpClient.DisconnectAsync(true, Arg.Any<CancellationToken>())
            .ThrowsAsync(new SocketException((int)SocketError.ConnectionAborted));

        var sut = new SmtpEmailSender(
            CreateOptions(),
            _logger,
            () => _mockSmtpClient);

        var act = () => sut.Send(CreateSampleMessage(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(primaryException);
    }

    [Fact]
    public async Task Send_WhenSendSucceedsAndDisconnectFails_ShouldLogSafeWarningWithoutSensitiveData()
    {
        _mockSmtpClient.IsConnected.Returns(true);
        _mockSmtpClient.DisconnectAsync(true, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SSL stream reset"));

        var sut = new SmtpEmailSender(
            CreateOptions(),
            _logger,
            () => _mockSmtpClient);

        var message = CreateSampleMessage();
        await sut.Send(message, CancellationToken.None);

        _logger.Logs.Should().Contain(l =>
            l.Level == LogLevel.Warning
            && l.Message.Contains("SMTP disconnect")
            && l.Message.Contains("sendCompleted=True")
            && !l.Message.Contains(message.RecipientAddress)
            && !l.Message.Contains(message.Subject)
            && !l.Message.Contains(message.TextBody));
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }
}
