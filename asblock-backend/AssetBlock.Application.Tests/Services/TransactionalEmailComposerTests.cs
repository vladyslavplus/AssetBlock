using AssetBlock.Application.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Services;

public sealed class TransactionalEmailComposerTests
{
    private readonly TransactionalEmailComposer _sut = new(Microsoft.Extensions.Options.Options.Create(CreateOptions()));

    [Fact]
    public void CreatePurchaseReceipt_WhenValid_ShouldBuildEncodedMultipartContent()
    {
        var purchasedAt = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        EmailDispatchPayload payload = _sut.CreatePurchaseReceipt(
            "buyer@example.com",
            Guid.NewGuid(),
            "Pack <script>alert(1)</script>",
            purchasedAt);

        payload.TemplateKind.Should().Be(EmailTemplateKind.PURCHASE_RECEIPT);
        payload.Subject.Should().Contain("Purchase receipt");
        payload.Subject.Should().NotContain("\r").And.NotContain("\n");
        payload.TextBody.Should().Contain("Pack <script>alert(1)</script>");
        payload.TextBody.Should().Contain("http://localhost:3000/library");
        payload.HtmlBody.Should().Contain("Pack &lt;script&gt;alert(1)&lt;/script&gt;");
        payload.HtmlBody.Should().Contain("http://localhost:3000/library");
        payload.HtmlBody.Should().NotContain("<script>alert(1)</script>");
    }

    [Fact]
    public void CreateAssetSold_WhenValid_ShouldUseSellerListingsUrl()
    {
        EmailDispatchPayload payload = _sut.CreateAssetSold(
            "author@example.com",
            Guid.NewGuid(),
            "My Asset",
            DateTimeOffset.UtcNow);

        payload.TemplateKind.Should().Be(EmailTemplateKind.ASSET_SOLD);
        payload.TextBody.Should().Contain("http://localhost:3000/sell");
        payload.HtmlBody.Should().Contain("http://localhost:3000/sell");
        payload.Subject.Should().StartWith("Asset sold:");
    }

    [Theory]
    [InlineData("https://app.test/base")]
    [InlineData("https://app.test?x=1")]
    [InlineData("https://app.test#frag")]
    [InlineData("https://user:pass@app.test")]
    public void CreatePurchaseReceipt_WhenPublicAppBaseUrlNotOrigin_ShouldThrow(string baseUrl)
    {
        var sut = new TransactionalEmailComposer(Microsoft.Extensions.Options.Options.Create(CreateOptions(baseUrl)));

        Func<EmailDispatchPayload> act = () => sut.CreatePurchaseReceipt(
            "buyer@example.com",
            Guid.NewGuid(),
            "Pack",
            DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>().WithMessage("*PublicAppBaseUrl*");
    }

    [Fact]
    public void CreatePurchaseReceipt_WhenTitleEmpty_ShouldThrow()
    {
        Func<EmailDispatchPayload> act = () => _sut.CreatePurchaseReceipt(
            "buyer@example.com",
            Guid.NewGuid(),
            "  ",
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>().WithParameterName("assetTitle");
    }

    [Fact]
    public void CreatePurchaseReceipt_WhenRecipientInvalid_ShouldThrow()
    {
        Func<EmailDispatchPayload> act = () => _sut.CreatePurchaseReceipt(
            "not-an-email",
            Guid.NewGuid(),
            "Pack",
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>().WithParameterName("recipientAddress");
    }

    [Fact]
    public void CreatePurchaseReceipt_WhenSubjectWouldExceedLimit_ShouldTruncateSubject()
    {
        var title = new string('A', EmailContentLimits.MAX_SUBJECT_LENGTH);
        EmailDispatchPayload payload = _sut.CreatePurchaseReceipt(
            "buyer@example.com",
            Guid.NewGuid(),
            title,
            DateTimeOffset.UtcNow);

        payload.Subject.Length.Should().BeLessThanOrEqualTo(EmailContentLimits.MAX_SUBJECT_LENGTH);
    }

    [Fact]
    public void CreateOrderReceipt_WhenValid_ShouldIncludeAmountItemsAndLibraryUrl()
    {
        var purchasedAt = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        EmailDispatchPayload payload = _sut.CreateOrderReceipt(
            "buyer@example.com",
            Guid.NewGuid(),
            "Bundle <b>Deal</b>",
            19.99m,
            "usd",
            purchasedAt,
            ["Alpha", "Beta <script>"]);

        payload.TemplateKind.Should().Be(EmailTemplateKind.ORDER_RECEIPT);
        payload.Subject.Should().Contain("Order receipt");
        payload.TextBody.Should().Contain("19.99 USD");
        payload.TextBody.Should().Contain("Purchased at (UTC):");
        payload.TextBody.Should().NotContain("Sold at (UTC):");
        payload.TextBody.Should().Contain("- Alpha");
        payload.TextBody.Should().Contain("- Beta <script>");
        payload.TextBody.Should().Contain("http://localhost:3000/library");
        payload.HtmlBody.Should().Contain("Purchased at (UTC):");
        payload.HtmlBody.Should().NotContain("Sold at (UTC):");
        payload.HtmlBody.Should().Contain("Bundle &lt;b&gt;Deal&lt;/b&gt;");
        payload.HtmlBody.Should().Contain("Beta &lt;script&gt;");
        payload.HtmlBody.Should().NotContain("<script>");
    }

    [Fact]
    public void CreateOrderSold_WhenValid_ShouldUseSellerListingsUrl()
    {
        EmailDispatchPayload payload = _sut.CreateOrderSold(
            "author@example.com",
            Guid.NewGuid(),
            "My Bundle",
            29.50m,
            "usd",
            DateTimeOffset.UtcNow,
            ["One", "Two"]);

        payload.TemplateKind.Should().Be(EmailTemplateKind.ORDER_SOLD);
        payload.Subject.Should().StartWith("Order sold:");
        payload.TextBody.Should().Contain("29.50 USD");
        payload.TextBody.Should().Contain("http://localhost:3000/sell");
        payload.HtmlBody.Should().Contain("http://localhost:3000/sell");
    }

    [Fact]
    public void CreateRegistrationAttemptNotice_WhenValid_ShouldBuildSecurityNoticeWithoutAccountData()
    {
        EmailDispatchPayload payload = _sut.CreateRegistrationAttemptNotice(
            "existing@example.com",
            Guid.NewGuid());

        payload.TemplateKind.Should().Be(EmailTemplateKind.REGISTRATION_ATTEMPT_NOTICE);
        payload.Subject.Should().Contain("registration attempt");
        payload.TextBody.Should().Contain("existing account was not changed");
        payload.HtmlBody.Should().Contain("existing account was not changed");
        payload.TextBody.Contains("password", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void CreateOrderReceipt_WhenAmountNotPositive_ShouldThrow()
    {
        Func<EmailDispatchPayload> act = () => _sut.CreateOrderReceipt(
            "buyer@example.com",
            Guid.NewGuid(),
            "Pack",
            0m,
            "usd",
            DateTimeOffset.UtcNow,
            ["A"]);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("amountTotal");
    }

    private static EmailOptions CreateOptions(string publicAppBaseUrl = "http://localhost:3000") => new()
    {
        Provider = "Smtp",
        FromName = "AssetBlock",
        FromAddress = "noreply@localhost",
        PublicAppBaseUrl = publicAppBaseUrl,
        MessageIdDomain = "mail.localhost",
        Smtp = new EmailSmtpOptions
        {
            Host = "localhost",
            Port = 1025,
            Security = SmtpSecurityMode.NONE,
            TimeoutSeconds = 30
        }
    };
}
