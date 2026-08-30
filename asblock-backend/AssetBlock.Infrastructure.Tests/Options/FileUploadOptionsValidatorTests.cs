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

    [Fact]
    public void Validate_WhenRarIsAllowed_ShouldFail()
    {
        var result = _sut.Validate(null, new FileUploadOptions { AllowedExtensions = [".zip", ".rar"] });
        result.Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData(".tar.gz")]
    [InlineData(".unitypackage")]
    [InlineData(".ZIP")]
    [InlineData(".7zip")]
    public void Validate_WhenValidMultipartOrAlphanumericExtensions_ShouldSucceed(string validExt)
    {
        var result = _sut.Validate(null, new FileUploadOptions { AllowedExtensions = [validExt] });
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".tar..gz")]
    [InlineData(".tar.gz.")]
    [InlineData(".tar/gz")]
    [InlineData(".tar\\gz")]
    [InlineData(".zip\"")]
    [InlineData(".zip'")]
    [InlineData(".zip`")]
    [InlineData(".zip\0")]
    [InlineData(".кириллица")]
    [InlineData(".zip space")]
    public void Validate_WhenInvalidExtensionGrammar_ShouldFail(string invalidExt)
    {
        var result = _sut.Validate(null, new FileUploadOptions { AllowedExtensions = [invalidExt] });
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenExtensionOversized_ShouldFail()
    {
        var longExt = "." + new string('a', 33);
        var result = _sut.Validate(null, new FileUploadOptions { AllowedExtensions = [longExt] });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("exceeds maximum length");
    }
}
