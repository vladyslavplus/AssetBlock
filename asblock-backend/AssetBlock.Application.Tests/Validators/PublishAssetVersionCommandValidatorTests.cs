using AssetBlock.Application.UseCases.Assets.PublishAssetVersion;
using AssetBlock.Domain.Core.Dto.Assets;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class PublishAssetVersionCommandValidatorTests
{
    private readonly PublishAssetVersionCommandValidator _validator = new();

    private static PublishAssetVersionCommand ValidCommand(string? releaseNotes = "Ship it") =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new PublishAssetVersionRequest("PERSONAL", releaseNotes!),
            new MemoryStream([1]),
            "file.zip",
            1);

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
}
