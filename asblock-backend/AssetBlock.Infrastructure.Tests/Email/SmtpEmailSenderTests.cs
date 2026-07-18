using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Email;
using MimeKit;

namespace AssetBlock.Infrastructure.Tests.Email;

public sealed class SmtpEmailSenderTests
{
    [Fact]
    public void BuildMimeMessage_WhenValid_ShouldSetFromToSubjectMultipartAndMessageId()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new EmailOptions
        {
            Provider = "Smtp",
            FromName = "AssetBlock",
            FromAddress = "noreply@localhost",
            PublicAppBaseUrl = "http://localhost:3000",
            MessageIdDomain = "mail.localhost",
            Smtp = new EmailSmtpOptions { Host = "localhost", Port = 1025, Security = SmtpSecurityMode.NONE, TimeoutSeconds = 30 }
        });
        var sut = new SmtpEmailSender(options);
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
}
