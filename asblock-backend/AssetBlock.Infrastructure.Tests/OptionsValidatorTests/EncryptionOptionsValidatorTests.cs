using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class EncryptionOptionsValidatorTests
{
    private readonly EncryptionOptionsValidator _sut = new();

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
    public void Validate_WhenKeysEmpty_ShouldFail()
    {
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>()
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("at least one configured encryption key");
    }

    [Fact]
    public void Validate_WhenCurrentKeyIdEmpty_ShouldFail()
    {
        var key = Convert.ToBase64String(new byte[EncryptionOptionsValidator.AES_256_KEY_LENGTH_BYTES]);
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = key
            }
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("CurrentKeyId must be specified");
    }

    [Fact]
    public void Validate_WhenCurrentKeyIdWhitespace_ShouldFail()
    {
        var key = Convert.ToBase64String(new byte[EncryptionOptionsValidator.AES_256_KEY_LENGTH_BYTES]);
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "   ",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = key
            }
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("CurrentKeyId must be specified");
    }

    [Fact]
    public void Validate_WhenCurrentKeyIdMissingFromKeys_ShouldFail()
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
        result.FailureMessage.Should().Contain("not found in Encryption:Keys");
    }

    [Fact]
    public void Validate_WhenKeyIdExceedsMaxBytes_ShouldFail()
    {
        var key = Convert.ToBase64String(new byte[EncryptionOptionsValidator.AES_256_KEY_LENGTH_BYTES]);
        var longKeyId = new string('a', 65);
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = longKeyId,
            Keys = new Dictionary<string, string>
            {
                [longKeyId] = key
            }
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("exceeds maximum length of 64 bytes");
    }

    [Fact]
    public void Validate_WhenKeyringContainsEmptyKeyId_ShouldFail()
    {
        var key = Convert.ToBase64String(new byte[EncryptionOptionsValidator.AES_256_KEY_LENGTH_BYTES]);
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = key,
                [""] = key
            }
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("empty key identifier");
    }

    [Fact]
    public void Validate_WhenKeyringKeyEmpty_ShouldFail()
    {
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = ""
            }
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("non-empty");
    }

    [Fact]
    public void Validate_WhenKeyringKeyInvalidBase64_ShouldFail()
    {
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = "not-valid-base64!!"
            }
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Base64");
    }

    [Fact]
    public void Validate_WhenKeyringKeyDecodedLengthNot32Bytes_ShouldFail()
    {
        var result = _sut.Validate(null, new EncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = new Dictionary<string, string>
            {
                ["k1"] = Convert.ToBase64String(new byte[16])
            }
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("32 bytes");
    }
}
