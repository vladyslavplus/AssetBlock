using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.Options;

public sealed class FileUploadOptionsValidatorTests
{
    private readonly FileUploadOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenDefaults_ShouldSucceed()
    {
        var result = _sut.Validate(null, new FileUploadOptions());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenMaxFileBytesInvalid_ShouldFail()
    {
        var result = _sut.Validate(null, new FileUploadOptions { MaxFileBytes = 0 });
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenExtensionMissingDot_ShouldFail()
    {
        var result = _sut.Validate(null, new FileUploadOptions { AllowedExtensions = ["zip"] });
        result.Failed.Should().BeTrue();
    }
}
