using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.Options;

public sealed class DataProtectionOptionsValidatorTests
{
    private readonly DataProtectionOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenCertificateModeWithoutCert_ShouldFail()
    {
        var result = _sut.Validate(null, new DataProtectionOptions
        {
            KeysPath = "dataprotection-keys",
            ProtectionMode = DataProtectionOptions.MODE_CERTIFICATE
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("CertificatePath");
    }

    [Fact]
    public void Validate_WhenKmsModeWithoutKeyId_ShouldFail()
    {
        var result = _sut.Validate(null, new DataProtectionOptions
        {
            KeysPath = "dataprotection-keys",
            ProtectionMode = DataProtectionOptions.MODE_KMS
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("KmsKeyId");
    }

    [Fact]
    public void Validate_WhenValidKeysPathAndEmptyMode_ShouldSucceed()
    {
        var result = _sut.Validate(null, new DataProtectionOptions
        {
            KeysPath = "dataprotection-keys",
            ProtectionMode = string.Empty
        });

        result.Succeeded.Should().BeTrue();
    }
}
