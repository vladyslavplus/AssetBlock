using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Email;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace AssetBlock.Infrastructure.IntegrationTests.Email;

/// <summary>Real SMTP delivery against pinned Mailpit; isolated from PostgreSQL fixture.</summary>
public sealed class SmtpEmailSenderMailpitTests : IAsyncLifetime
{
    private const string MAILPIT_IMAGE = "axllent/mailpit:v1.30.0";
    private const string MESSAGE_ID_DOMAIN = "mail.integration.test";

    private IContainer? _mailpit;
    private HttpClient? _http;

    public async Task InitializeAsync()
    {
        _mailpit = new ContainerBuilder()
            .WithImage(MAILPIT_IMAGE)
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/api/v1/info")))
            .Build();

        await _mailpit.StartAsync();
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}/")
        };
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_mailpit is not null)
        {
            await _mailpit.DisposeAsync();
        }
    }

    [Fact]
    public async Task Send_WhenMailpitReceivesSmtp_ShouldCaptureRecipientSubjectMultipartAndMessageId()
    {
        var smtpPort = _mailpit!.GetMappedPublicPort(1025);
        var options = Microsoft.Extensions.Options.Options.Create(new EmailOptions
        {
            Provider = "Smtp",
            FromName = "AssetBlock",
            FromAddress = "noreply@localhost",
            PublicAppBaseUrl = "http://localhost:3000",
            MessageIdDomain = MESSAGE_ID_DOMAIN,
            Smtp = new EmailSmtpOptions
            {
                Host = _mailpit.Hostname,
                Port = smtpPort,
                Security = SmtpSecurityMode.NONE,
                TimeoutSeconds = 30
            }
        });

        var outboxId = Guid.NewGuid();
        var messageId = $"<{outboxId:N}@{MESSAGE_ID_DOMAIN}>";
        var message = new EmailMessage(
            "buyer@example.com",
            Guid.NewGuid(),
            "Purchase receipt: Pack",
            "Thanks for your purchase.\nOpen http://localhost:3000/library",
            "<p>Thanks for your purchase.</p><p><a href=\"http://localhost:3000/library\">Open your library</a></p>",
            EmailTemplateKind.PURCHASE_RECEIPT,
            messageId);

        var sender = new SmtpEmailSender(options);
        await sender.Send(message, CancellationToken.None);

        MailpitMessageSummary? summary = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var list = await _http!.GetFromJsonAsync<MailpitMessagesResponse>("api/v1/messages");
            summary = list?.Messages.FirstOrDefault(m =>
                m.To.Any(t => t.Address.Equals("buyer@example.com", StringComparison.OrdinalIgnoreCase))
                && m.Subject == "Purchase receipt: Pack");
            if (summary is not null)
            {
                break;
            }

            await Task.Delay(250);
        }

        summary.Should().NotBeNull("Mailpit should capture the SMTP message");
        var detail = await _http!.GetFromJsonAsync<MailpitMessageDetail>($"api/v1/message/{summary.Id}");
        detail.Should().NotBeNull();
        detail.Subject.Should().Be("Purchase receipt: Pack");
        detail.To.Should().Contain(t => t.Address.Equals("buyer@example.com", StringComparison.OrdinalIgnoreCase));
        detail.Text.Should().Contain("Thanks for your purchase");
        detail.Text.Should().Contain("http://localhost:3000/library");
        detail.Html.Should().Contain("Thanks for your purchase");
        detail.Html.Should().Contain("http://localhost:3000/library");
        detail.MessageId.Should().Be($"{outboxId:N}@{MESSAGE_ID_DOMAIN}");
    }

    private sealed class MailpitMessagesResponse
    {
        [JsonPropertyName("messages")]
        public List<MailpitMessageSummary> Messages { get; set; } = [];
    }

    private sealed class MailpitMessageSummary
    {
        [JsonPropertyName("ID")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("Subject")]
        public string Subject { get; set; } = string.Empty;

        [JsonPropertyName("To")]
        public List<MailpitAddress> To { get; set; } = [];
    }

    private sealed class MailpitMessageDetail
    {
        [JsonPropertyName("Subject")]
        public string Subject { get; set; } = string.Empty;

        [JsonPropertyName("To")]
        public List<MailpitAddress> To { get; set; } = [];

        [JsonPropertyName("Text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("HTML")]
        public string Html { get; set; } = string.Empty;

        [JsonPropertyName("MessageID")]
        public string MessageId { get; set; } = string.Empty;
    }

    private sealed class MailpitAddress
    {
        [JsonPropertyName("Address")]
        public string Address { get; set; } = string.Empty;
    }
}
