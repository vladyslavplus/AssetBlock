using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class JwtOptionsValidatorTests
{
    private readonly JwtOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenConfigValid_ShouldSucceed()
    {
        ValidateOptionsResult result = _sut.Validate(null, CreateValid());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRequiredFieldsEmpty_ShouldFail()
    {
        ValidateOptionsResult result = _sut.Validate(null, new JwtOptions
        {
            Issuer = "",
            Audience = " ",
            Key = "",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Issuer"));
        result.Failures.Should().Contain(m => m.Contains("Audience"));
        result.Failures.Should().Contain(m => m.Contains("Key"));
    }

    [Fact]
    public void Validate_WhenSigningKeyTooShort_ShouldFail()
    {
        JwtOptions options = CreateValid();
        options.Key = new string('k', JwtOptionsValidator.MIN_SIGNING_KEY_LENGTH - 1);

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("at least"));
    }

    [Fact]
    public void Validate_WhenKeyIsPlaceholder_ShouldFail()
    {
        JwtOptions options = CreateValid();
        options.Key = "<dev-secret-key-min-32-characters-long-for-hmac>";

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Key"));
    }

    [Fact]
    public void Validate_WhenTokenLifetimesInvalid_ShouldFail()
    {
        JwtOptions options = CreateValid();
        options.AccessTokenMinutes = 0;
        options.RefreshTokenDays = -1;

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("AccessTokenMinutes"));
        result.Failures.Should().Contain(m => m.Contains("RefreshTokenDays"));
    }

    [Fact]
    public void Validate_WhenHubAudienceMissing_ShouldFail()
    {
        JwtOptions options = CreateValid();
        options.HubAudience = "";

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("HubAudience"));
    }

    [Fact]
    public void Validate_WhenHubAudienceSameAsRestAudience_ShouldFail()
    {
        JwtOptions options = CreateValid();
        options.HubAudience = options.Audience;

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("HubAudience"));
    }

    [Theory]
    [InlineData(59)]
    [InlineData(121)]
    public void Validate_WhenHubTokenSecondsOutOfRange_ShouldFail(int seconds)
    {
        JwtOptions options = CreateValid();
        options.HubTokenSeconds = seconds;

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("HubTokenSeconds"));
    }

    private static JwtOptions CreateValid() => new()
    {
        Issuer = "AssetBlock",
        Audience = "AssetBlock.Api",
        Key = new string('k', JwtOptionsValidator.MIN_SIGNING_KEY_LENGTH),
        AccessTokenMinutes = 15,
        RefreshTokenDays = 7,
        HubAudience = "AssetBlock.Hub",
        HubTokenSeconds = 90
    };
}
