using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using IOptionsEmail = Microsoft.Extensions.Options.IOptions<AssetBlock.Domain.Core.Primitives.AppSettingsOptions.EmailOptions>;
using OptionsHelper = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.Tests.Email;

public sealed class EmailActionLinkProtectorTests : IDisposable
{
    private readonly string _tempKeysPath;
    private readonly EmailActionLinkProtector _sut;

    public EmailActionLinkProtectorTests()
    {
        _tempKeysPath = Path.Combine(Path.GetTempPath(), "assetblock-dp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempKeysPath);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(_tempKeysPath));
        var sp = services.BuildServiceProvider();

        IOptionsEmail emailOptions = OptionsHelper.Create(new EmailOptions
        {
            Provider = "Smtp",
            FromName = "AssetBlock",
            FromAddress = "noreply@localhost",
            PublicAppBaseUrl = "http://localhost:3000",
            MessageIdDomain = "mail.localhost",
            Smtp = new EmailSmtpOptions { Host = "localhost", Port = 1025, Security = SmtpSecurityMode.NONE, TimeoutSeconds = 30 }
        });

        var dataProtectionProvider = sp.GetRequiredService<IDataProtectionProvider>();
        _sut = new EmailActionLinkProtector(
            dataProtectionProvider,
            emailOptions,
            NullLogger<EmailActionLinkProtector>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempKeysPath))
        {
            Directory.Delete(_tempKeysPath, recursive: true);
        }
    }

    [Fact]
    public void Protect_ThenTryUnprotect_ShouldRoundTrip()
    {
        var claims = new EmailActionLinkClaims(
            Guid.NewGuid(),
            Guid.NewGuid(),
            EmailActionPurpose.EMAIL_VERIFICATION,
            DateTimeOffset.UtcNow.AddHours(24));

        var token = _sut.Protect(claims);

        token.Should().NotBeNullOrWhiteSpace();
        var success = _sut.TryUnprotect(token, EmailActionPurpose.EMAIL_VERIFICATION, out var decoded);

        success.Should().BeTrue();
        decoded.ActionId.Should().Be(claims.ActionId);
        decoded.Version.Should().Be(claims.Version);
        decoded.Purpose.Should().Be(EmailActionPurpose.EMAIL_VERIFICATION);
    }

    [Fact]
    public void TryUnprotect_WhenWrongPurpose_ShouldReturnFalse()
    {
        var claims = new EmailActionLinkClaims(
            Guid.NewGuid(),
            Guid.NewGuid(),
            EmailActionPurpose.PASSWORD_RESET,
            DateTimeOffset.UtcNow.AddMinutes(30));

        var token = _sut.Protect(claims);
        var success = _sut.TryUnprotect(token, EmailActionPurpose.EMAIL_VERIFICATION, out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void TryUnprotect_WhenTamperedToken_ShouldReturnFalse()
    {
        var claims = new EmailActionLinkClaims(
            Guid.NewGuid(),
            Guid.NewGuid(),
            EmailActionPurpose.EMAIL_VERIFICATION,
            DateTimeOffset.UtcNow.AddHours(24));

        var token = _sut.Protect(claims);
        var tampered = token[..^4] + "XXXX";

        var success = _sut.TryUnprotect(tampered, EmailActionPurpose.EMAIL_VERIFICATION, out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void TryUnprotect_WhenEmptyToken_ShouldReturnFalse()
    {
        var success = _sut.TryUnprotect(string.Empty, EmailActionPurpose.EMAIL_VERIFICATION, out _);
        success.Should().BeFalse();
    }

    [Theory]
    [InlineData(EmailActionPurpose.EMAIL_VERIFICATION, "/verify-email")]
    [InlineData(EmailActionPurpose.PASSWORD_RESET, "/reset-password")]
    [InlineData(EmailActionPurpose.EMAIL_CHANGE, "/confirm-email-change")]
    public void BuildActionUrl_ShouldContainCorrectPathAndToken(EmailActionPurpose purpose, string expectedPath)
    {
        const string token = "test-token-abc123";
        var url = _sut.BuildActionUrl(purpose, token);

        url.Should().StartWith("http://localhost:3000");
        url.Should().Contain(expectedPath);
        url.Should().Contain("#token=");
        url.Should().NotContain("?token=");
        url.Should().Contain(Uri.EscapeDataString(token));
    }

    [Fact]
    public void Protect_WhenClaimsAlreadyExpired_ShouldThrow()
    {
        var claims = new EmailActionLinkClaims(
            Guid.NewGuid(),
            Guid.NewGuid(),
            EmailActionPurpose.EMAIL_VERIFICATION,
            DateTimeOffset.UtcNow.AddSeconds(-1));

        var act = () => _sut.Protect(claims);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*expired*");
    }

    [Fact]
    public void Protect_ThenUnprotect_AcrossNewServiceProvider_ShouldSucceedWithPersistedKeyRing()
    {
        var keysPath = Path.Combine(Path.GetTempPath(), $"assetblock-dataprotection-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysPath);

        try
        {
            var claims = new EmailActionLinkClaims(
                Guid.NewGuid(),
                Guid.NewGuid(),
                EmailActionPurpose.PASSWORD_RESET,
                DateTimeOffset.UtcNow.AddMinutes(30));

            string token;
            using (var first = BuildProtectorProvider(keysPath))
            {
                var protector = CreateProtector(first);
                token = protector.Protect(claims);
            }

            using var second = BuildProtectorProvider(keysPath);
            var restarted = CreateProtector(second);
            var success = restarted.TryUnprotect(token, EmailActionPurpose.PASSWORD_RESET, out var decoded);

            success.Should().BeTrue();
            decoded.ActionId.Should().Be(claims.ActionId);
            decoded.Version.Should().Be(claims.Version);
        }
        finally
        {
            if (Directory.Exists(keysPath))
            {
                Directory.Delete(keysPath, recursive: true);
            }
        }
    }

    private static ServiceProvider BuildProtectorProvider(string keysPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .SetApplicationName("AssetBlock")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        return services.BuildServiceProvider();
    }

    private static EmailActionLinkProtector CreateProtector(ServiceProvider sp)
    {
        IOptionsEmail emailOptions = OptionsHelper.Create(new EmailOptions
        {
            Provider = "Smtp",
            FromName = "AssetBlock",
            FromAddress = "noreply@localhost",
            PublicAppBaseUrl = "http://localhost:3000",
            MessageIdDomain = "mail.localhost",
            Smtp = new EmailSmtpOptions { Host = "localhost", Port = 1025, Security = SmtpSecurityMode.NONE, TimeoutSeconds = 30 }
        });

        return new EmailActionLinkProtector(
            sp.GetRequiredService<IDataProtectionProvider>(),
            emailOptions,
            NullLogger<EmailActionLinkProtector>.Instance);
    }
}
