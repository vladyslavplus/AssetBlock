using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class EncryptionOptionsValidatorTests
{
    private readonly EncryptionOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenConfigValid_ShouldSucceed()
    {
        var result = _sut.Validate(null, new EncryptionOptions
        {
            KeyBase64 = Convert.ToBase64String(new byte[EncryptionOptionsValidator.AES_256_KEY_LENGTH_BYTES])
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenKeyEmpty_ShouldFail()
    {
        var result = _sut.Validate(null, new EncryptionOptions { KeyBase64 = "" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("non-empty");
    }

    [Fact]
    public void Validate_WhenBase64Invalid_ShouldFail()
    {
        var result = _sut.Validate(null, new EncryptionOptions { KeyBase64 = "not-valid-base64!!" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Base64");
    }

    [Fact]
    public void Validate_WhenDecodedLengthWrong_ShouldFail()
    {
        var result = _sut.Validate(null, new EncryptionOptions
        {
            KeyBase64 = Convert.ToBase64String(new byte[16])
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("32 bytes");
    }
}
