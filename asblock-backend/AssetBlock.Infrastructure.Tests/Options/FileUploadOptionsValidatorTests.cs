using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests.Options;

public sealed class FileUploadOptionsValidatorTests
{
    private readonly FileUploadOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenDefaults_ShouldSucceed()
    {
        ValidateOptionsResult result = _sut.Validate(null, new FileUploadOptions());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenMaxFileBytesInvalid_ShouldFail()
    {
        ValidateOptionsResult result = _sut.Validate(null, new FileUploadOptions { MaxFileBytes = 0 });
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenExtensionMissingDot_ShouldFail()
    {
        ValidateOptionsResult result = _sut.Validate(null, new FileUploadOptions { AllowedExtensions = ["zip"] });
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRarIsAllowed_ShouldFail()
    {
        ValidateOptionsResult result = _sut.Validate(null, new FileUploadOptions { AllowedExtensions = [".zip", ".rar"] });
        result.Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData(".tar.gz")]
    [InlineData(".unitypackage")]
    [InlineData(".ZIP")]
    [InlineData(".7zip")]
    public void Validate_WhenValidMultipartOrAlphanumericExtensions_ShouldSucceed(string validExt)
    {
        ValidateOptionsResult result = _sut.Validate(null, new FileUploadOptions { AllowedExtensions = [validExt] });
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
        ValidateOptionsResult result = _sut.Validate(null, new FileUploadOptions { AllowedExtensions = [invalidExt] });
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenExtensionOversized_ShouldFail()
    {
        var longExt = "." + new string('a', 33);
        ValidateOptionsResult result = _sut.Validate(null, new FileUploadOptions { AllowedExtensions = [longExt] });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("exceeds maximum length");
    }
}
