using AssetBlock.Application.UseCases.Assets.PublishAssetVersion;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class PublishAssetVersionCommandValidatorTests
{
    private readonly PublishAssetVersionCommandValidator _validator = new(Microsoft.Extensions.Options.Options.Create(new FileUploadOptions()));

    private static PublishAssetVersionCommand ValidCommand(
        string? releaseNotes = "Ship it",
        string fileName = "file.zip",
        long fileLength = 1,
        string licenseCode = "PERSONAL") =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new PublishAssetVersionRequest(licenseCode, releaseNotes!),
            new MemoryStream([1]),
            fileName,
            fileLength);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public async Task Validate_WhenReleaseNotesNullOrWhitespace_ShouldFail(string? releaseNotes)
    {
        var result = await _validator.ValidateAsync(ValidCommand(releaseNotes));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("ReleaseNotes"));
    }

    [Fact]
    public async Task Validate_WhenReleaseNotesExactly4000TrimmedChars_ShouldPass()
    {
        var notes = $"  {new string('a', 4000)}  ";
        var result = await _validator.ValidateAsync(ValidCommand(notes));

        result.Errors.Should().NotContain(e => e.PropertyName.Contains("ReleaseNotes"));
    }

    [Fact]
    public async Task Validate_WhenReleaseNotesExceeds4000TrimmedChars_ShouldFail()
    {
        var notes = $"  {new string('a', 4001)}  ";
        var result = await _validator.ValidateAsync(ValidCommand(notes));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName.Contains("ReleaseNotes") &&
            e.ErrorMessage.Contains("4000"));
    }

    [Fact]
    public async Task Validate_WhenFileLengthExceedsMaxBytes_ShouldFail()
    {
        var result = await _validator.ValidateAsync(ValidCommand(fileLength: 250L * 1024 * 1024 + 1));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("FileLength"));
    }

    [Fact]
    public async Task Validate_WhenExtensionNotAllowed_ShouldFail()
    {
        var result = await _validator.ValidateAsync(ValidCommand(fileName: "image.png"));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("FileName"));
    }

    [Fact]
    public async Task Validate_WhenLicenseCodeIsInvalid_ShouldFail()
    {
        var result = await _validator.ValidateAsync(ValidCommand(licenseCode: "INVALID"));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("LicenseCode"));
    }
}
