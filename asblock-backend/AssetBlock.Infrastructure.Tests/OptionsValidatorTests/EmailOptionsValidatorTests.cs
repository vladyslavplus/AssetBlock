using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class EmailOptionsValidatorTests
{
    private readonly EmailOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenMailpitLocalConfigValid_ShouldSucceed()
    {
        var result = _sut.Validate(null, CreateValidMailpit());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPlaceholders_ShouldFail()
    {
        var options = CreateValidMailpit();
        options.FromName = "<AssetBlock>";
        options.FromAddress = "<noreply@example.com>";
        options.PublicAppBaseUrl = "<public-app-base-url>";
        options.MessageIdDomain = "<message-id-domain>";
        options.Smtp.Host = "<smtp-host>";

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("FromName"));
        result.Failures.Should().Contain(m => m.Contains("FromAddress"));
        result.Failures.Should().Contain(m => m.Contains("PublicAppBaseUrl"));
        result.Failures.Should().Contain(m => m.Contains("MessageIdDomain"));
        result.Failures.Should().Contain(m => m.Contains("Host"));
    }

    [Fact]
    public void Validate_WhenProviderNotSmtp_ShouldFail()
    {
        var options = CreateValidMailpit();
        options.Provider = "Resend";

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Provider"));
    }

    [Fact]
    public void Validate_WhenPortInvalid_ShouldFail()
    {
        var options = CreateValidMailpit();
        options.Smtp.Port = 0;

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Port"));
    }

    [Fact]
    public void Validate_WhenSecurityUndefined_ShouldFail()
    {
        var options = CreateValidMailpit();
        options.Smtp.Security = (SmtpSecurityMode)999;

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Security"));
    }

    [Fact]
    public void Validate_WhenPartialCredentials_ShouldFail()
    {
        var options = CreateValidMailpit();
        options.Smtp.Username = "user";
        options.Smtp.Password = "";

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Username") && m.Contains("Password"));
    }

    [Fact]
    public void Validate_WhenBothCredentialsEmpty_ShouldSucceed()
    {
        var options = CreateValidMailpit();
        options.Smtp.Username = "";
        options.Smtp.Password = "";

        var result = _sut.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenBothCredentialsSet_ShouldSucceed()
    {
        var options = CreateValidMailpit();
        options.Smtp.Username = "user";
        options.Smtp.Password = "secret";

        var result = _sut.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenBothCredentialPlaceholders_ShouldFail()
    {
        var options = CreateValidMailpit();
        options.Smtp.Username = "<user>";
        options.Smtp.Password = "<password>";

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("placeholders"));
    }

    [Fact]
    public void Validate_WhenUsernamePlaceholderAndRealPassword_ShouldFail()
    {
        var options = CreateValidMailpit();
        options.Smtp.Username = "<user>";
        options.Smtp.Password = "secret";

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("placeholders"));
    }

    [Fact]
    public void Validate_WhenRealUsernameAndPasswordPlaceholder_ShouldFail()
    {
        var options = CreateValidMailpit();
        options.Smtp.Username = "user";
        options.Smtp.Password = "<password>";

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("placeholders"));
    }

    [Fact]
    public void Validate_WhenTimeoutOutOfRange_ShouldFail()
    {
        var options = CreateValidMailpit();
        options.Smtp.TimeoutSeconds = EmailOptionsValidator.MAX_TIMEOUT_SECONDS + 1;

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("TimeoutSeconds"));
    }

    [Fact]
    public void Validate_WhenPublicAppBaseUrlIsOrigin_ShouldSucceed()
    {
        var options = CreateValidMailpit();
        options.PublicAppBaseUrl = "http://localhost:3000";

        var result = _sut.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://app.test/base")]
    [InlineData("https://app.test?x=1")]
    [InlineData("https://app.test#frag")]
    [InlineData("https://user:pass@app.test")]
    public void Validate_WhenPublicAppBaseUrlNotOrigin_ShouldFail(string url)
    {
        var options = CreateValidMailpit();
        options.PublicAppBaseUrl = url;

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("PublicAppBaseUrl"));
    }

    [Fact]
    public void Validate_WhenMessageIdDomainHasWhitespace_ShouldFail()
    {
        var options = CreateValidMailpit();
        options.MessageIdDomain = "mail localhost";

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("MessageIdDomain"));
    }

    [Theory]
    [InlineData("mail@localhost")]
    [InlineData("<mail.localhost>")]
    [InlineData("mail/localhost")]
    [InlineData("mail\\localhost")]
    [InlineData("mail:localhost")]
    [InlineData("mail?x=1")]
    [InlineData("mail#frag")]
    public void Validate_WhenMessageIdDomainInvalidHostSyntax_ShouldFail(string domain)
    {
        var options = CreateValidMailpit();
        options.MessageIdDomain = domain;

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("MessageIdDomain"));
    }

    [Fact]
    public void Validate_WhenMessageIdDomainIsMailLocalhost_ShouldSucceed()
    {
        var options = CreateValidMailpit();
        options.MessageIdDomain = "mail.localhost";

        var result = _sut.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    private static EmailOptions CreateValidMailpit() => new()
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
            Username = "",
            Password = "",
            TimeoutSeconds = 30
        }
    };
}
