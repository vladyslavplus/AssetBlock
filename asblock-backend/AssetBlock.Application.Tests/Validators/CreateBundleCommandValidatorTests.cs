using AssetBlock.Application.UseCases.Bundles.CreateBundle;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Payments;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public sealed class CreateBundleCommandValidatorTests
{
    private readonly CreateBundleCommandValidator _validator = new();

    private static CreateBundleCommand Valid(
        string title = "Starter Bundle",
        decimal price = 10m,
        IReadOnlyList<Guid>? assetIds = null) =>
        new(
            Guid.NewGuid(),
            title,
            "Desc",
            price,
            assetIds ?? [Guid.NewGuid(), Guid.NewGuid()]);

    [Fact]
    public async Task Validate_WhenAssetCountBelowMin_ShouldFail()
    {
        ValidationResult result = await _validator.ValidateAsync(Valid(assetIds: [Guid.NewGuid()]));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage == ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_BUNDLE_ASSET_COUNT_INVALID));
    }

    [Fact]
    public async Task Validate_WhenAssetCountAboveMax_ShouldFail()
    {
        var ids = Enumerable.Range(0, BundleConstants.MAX_ITEMS + 1).Select(_ => Guid.NewGuid()).ToList();
        ValidationResult result = await _validator.ValidateAsync(Valid(assetIds: ids));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage == ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_BUNDLE_ASSET_COUNT_INVALID));
    }

    [Fact]
    public async Task Validate_WhenDuplicateAssetIds_ShouldFail()
    {
        var id = Guid.NewGuid();
        ValidationResult result = await _validator.ValidateAsync(Valid(assetIds: [id, id]));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage == ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_BUNDLE_DUPLICATE_ASSET));
    }

    [Fact]
    public async Task Validate_WhenPriceNotPositive_ShouldFail()
    {
        ValidationResult result = await _validator.ValidateAsync(Valid(price: 0m));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBundleCommand.Price));
    }

    [Fact]
    public async Task Validate_WhenTitleExceedsMaxLength_ShouldFail()
    {
        var title = new string('x', BundleConstants.TITLE_MAX_LENGTH + 1);
        ValidationResult result = await _validator.ValidateAsync(Valid(title: title));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBundleCommand.Title));
    }

    [Fact]
    public async Task Validate_WhenBoundariesValid_ShouldPass()
    {
        var ids = Enumerable.Range(0, BundleConstants.MIN_ITEMS).Select(_ => Guid.NewGuid()).ToList();
        ValidationResult result = await _validator.ValidateAsync(Valid(assetIds: ids));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenPriceOverMaxAmount_ShouldFail()
    {
        ValidationResult result = await _validator.ValidateAsync(Valid(price: BundlePriceAllocator.MAX_AMOUNT + 0.01m));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBundleCommand.Price));
    }
}
