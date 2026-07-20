using AssetBlock.Application.UseCases.Assets.UploadAsset;
using AssetBlock.Domain.Core.Dto.Assets;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

/// <summary>
/// Tests for FluentValidation validators.
/// We instantiate validators directly — no DI needed.
/// </summary>
public class UploadAssetCommandValidatorTests
{
    private readonly UploadAssetCommandValidator _validator = new();

    private static UploadAssetCommand ValidCommand(
        string title = "My Asset",
        decimal price = 5m,
        string fileName = "file.zip",
        int? downloadLimitPerHour = null,
        long fileLength = 1) =>
        new(Guid.NewGuid(),
            new UploadAssetRequest(title, null, price, Guid.NewGuid(), "PERSONAL", downloadLimitPerHour),
            new MemoryStream([1]),
            fileName,
            fileLength);

    [Fact]
    public async Task Validate_WhenAuthorIdIsEmpty_ShouldFail()
    {
        var command = new UploadAssetCommand(
            Guid.Empty,
            new UploadAssetRequest("Title", null, 5m, Guid.NewGuid(), "PERSONAL"),
            new MemoryStream([1]),
            "file.zip",
            1);

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("AuthorId"));
    }

    [Fact]
    public async Task Validate_WhenTitleIsEmpty_ShouldFail()
    {
        var result = await _validator.ValidateAsync(ValidCommand(title: ""));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Title"));
    }

    [Fact]
    public async Task Validate_WhenTitleExceeds500Chars_ShouldFail()
    {
        var result = await _validator.ValidateAsync(ValidCommand(title: new string('A', 501)));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Title"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task Validate_WhenPriceIsNotPositive_ShouldFail(decimal price)
    {
        var result = await _validator.ValidateAsync(ValidCommand(price: price));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Price"));
    }

    [Fact]
    public async Task Validate_WhenPriceIsPositive_ShouldPass()
    {
        var result = await _validator.ValidateAsync(ValidCommand(price: 0.01m));
        result.Errors.Should().NotContain(e => e.PropertyName.Contains("Price"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Validate_WhenDownloadLimitIsZeroOrNegative_ShouldFail(int limit)
    {
        var result = await _validator.ValidateAsync(ValidCommand(downloadLimitPerHour: limit));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("DownloadLimitPerHour"));
    }

    [Fact]
    public async Task Validate_WhenDownloadLimitIsNull_ShouldPass()
    {
        var result = await _validator.ValidateAsync(ValidCommand(downloadLimitPerHour: null));
        result.Errors.Should().NotContain(e => e.PropertyName.Contains("DownloadLimitPerHour"));
    }

    [Fact]
    public async Task Validate_WhenDownloadLimitIsPositive_ShouldPass()
    {
        var result = await _validator.ValidateAsync(ValidCommand(downloadLimitPerHour: 10));
        result.Errors.Should().NotContain(e => e.PropertyName.Contains("DownloadLimitPerHour"));
    }

    [Fact]
    public async Task Validate_WhenFileNameIsEmpty_ShouldFail()
    {
        var result = await _validator.ValidateAsync(ValidCommand(fileName: ""));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("FileName"));
    }

    [Fact]
    public async Task Validate_WhenCommandIsValid_ShouldPass()
    {
        var result = await _validator.ValidateAsync(ValidCommand());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenDescriptionExceeds5000Chars_ShouldFail()
    {
        var longDesc = new string('x', 5001);
        var command = new UploadAssetCommand(
            Guid.NewGuid(),
            new UploadAssetRequest("Title", longDesc, 5m, Guid.NewGuid(), "PERSONAL"),
            new MemoryStream([1]),
            "file.zip",
            1);

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Description"));
    }
}
