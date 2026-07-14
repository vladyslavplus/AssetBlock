using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class JwtOptionsValidatorTests
{
    private readonly JwtOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenConfigValid_ShouldSucceed()
    {
        var result = _sut.Validate(null, CreateValid());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRequiredFieldsEmpty_ShouldFail()
    {
        var result = _sut.Validate(null, new JwtOptions
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
        var options = CreateValid();
        options.Key = new string('k', JwtOptionsValidator.MIN_SIGNING_KEY_LENGTH - 1);

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("at least"));
    }

    [Fact]
    public void Validate_WhenKeyIsPlaceholder_ShouldFail()
    {
        var options = CreateValid();
        options.Key = "<dev-secret-key-min-32-characters-long-for-hmac>";

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("Key"));
    }

    [Fact]
    public void Validate_WhenTokenLifetimesInvalid_ShouldFail()
    {
        var options = CreateValid();
        options.AccessTokenMinutes = 0;
        options.RefreshTokenDays = -1;

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("AccessTokenMinutes"));
        result.Failures.Should().Contain(m => m.Contains("RefreshTokenDays"));
    }

    private static JwtOptions CreateValid() => new()
    {
        Issuer = "AssetBlock",
        Audience = "AssetBlock.Api",
        Key = new string('k', JwtOptionsValidator.MIN_SIGNING_KEY_LENGTH),
        AccessTokenMinutes = 15,
        RefreshTokenDays = 7
    };
}
