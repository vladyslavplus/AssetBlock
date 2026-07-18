using AssetBlock.Application.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Services;

public sealed class TransactionalEmailComposerTests
{
    private readonly TransactionalEmailComposer _sut = new(Microsoft.Extensions.Options.Options.Create(CreateOptions()));

    [Fact]
    public void CreatePurchaseReceipt_WhenValid_ShouldBuildEncodedMultipartContent()
    {
        var purchasedAt = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var payload = _sut.CreatePurchaseReceipt(
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
        var payload = _sut.CreateAssetSold(
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

        var act = () => sut.CreatePurchaseReceipt(
            "buyer@example.com",
            Guid.NewGuid(),
            "Pack",
            DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>().WithMessage("*PublicAppBaseUrl*");
    }

    [Fact]
    public void CreatePurchaseReceipt_WhenTitleEmpty_ShouldThrow()
    {
        var act = () => _sut.CreatePurchaseReceipt(
            "buyer@example.com",
            Guid.NewGuid(),
            "  ",
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>().WithParameterName("assetTitle");
    }

    [Fact]
    public void CreatePurchaseReceipt_WhenRecipientInvalid_ShouldThrow()
    {
        var act = () => _sut.CreatePurchaseReceipt(
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
        var payload = _sut.CreatePurchaseReceipt(
            "buyer@example.com",
            Guid.NewGuid(),
            title,
            DateTimeOffset.UtcNow);

        payload.Subject.Length.Should().BeLessThanOrEqualTo(EmailContentLimits.MAX_SUBJECT_LENGTH);
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
