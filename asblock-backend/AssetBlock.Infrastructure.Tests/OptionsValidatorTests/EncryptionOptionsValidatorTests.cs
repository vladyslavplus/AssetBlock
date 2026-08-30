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

    [Fact]
    public void Validate_WhenKeyringValid_ShouldSucceed()
    {
        var key = Convert.ToBase64String(new byte[EncryptionOptionsValidator.AES_256_KEY_LENGTH_BYTES]);
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "k2",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = key,
                ["k2"] = key
            }
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenKeyringCurrentKeyIdMissingFromKeys_ShouldFail()
    {
        var key = Convert.ToBase64String(new byte[EncryptionOptionsValidator.AES_256_KEY_LENGTH_BYTES]);
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "k3",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = key,
                ["k2"] = key
            }
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("not found");
    }

    [Fact]
    public void Validate_WhenKeyringContainsInvalidKey_ShouldFail()
    {
        var key = Convert.ToBase64String(new byte[EncryptionOptionsValidator.AES_256_KEY_LENGTH_BYTES]);
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = key,
                ["k2"] = "invalid-base64"
            }
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Base64");
    }

    [Fact]
    public void Validate_WhenLegacyKeyIdValid_ShouldSucceed()
    {
        var key = Convert.ToBase64String(new byte[EncryptionOptionsValidator.AES_256_KEY_LENGTH_BYTES]);
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "k2",
            LegacyKeyId = "k1",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = key,
                ["k2"] = key
            }
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenLegacyKeyIdMissingFromKeys_ShouldFail()
    {
        var key = Convert.ToBase64String(new byte[EncryptionOptionsValidator.AES_256_KEY_LENGTH_BYTES]);
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "k1",
            LegacyKeyId = "missing-legacy",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = key
            }
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("LegacyKeyId 'missing-legacy' was not found");
    }
}
