using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsAssetDetail;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsBundleDetail;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsCollections;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public sealed class SellerAnalyticsDetailQueryValidatorTests
{
    private static readonly Guid _sellerId = Guid.NewGuid();
    private static readonly DateOnly _validFrom = new(2024, 1, 1);
    private static readonly DateOnly _validTo = new(2024, 1, 11);

    private readonly GetSellerAnalyticsAssetDetailQueryValidator _assetValidator = new();
    private readonly GetSellerAnalyticsBundleDetailQueryValidator _bundleValidator = new();
    private readonly GetSellerAnalyticsCollectionsQueryValidator _collectionsValidator = new();

    [Fact]
    public void Validate_WhenAssetDetailRangeValid_ShouldPass()
    {
        ValidationResult result = _assetValidator.Validate(
            new GetSellerAnalyticsAssetDetailQuery(_sellerId, Guid.NewGuid(), _validFrom, _validTo));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenBundleDetailRangeInvalid_ShouldFail()
    {
        ValidationResult result = _bundleValidator.Validate(
            new GetSellerAnalyticsBundleDetailQuery(_sellerId, Guid.NewGuid(), _validTo, _validFrom));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE));
    }

    [Fact]
    public void Validate_WhenCollectionsRangeInvalid_ShouldFail()
    {
        var request = new AnalyticsCollectionsRequest(_validTo, _validFrom);
        ValidationResult result = _collectionsValidator.Validate(new GetSellerAnalyticsCollectionsQuery(_sellerId, request));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE));
    }

    [Fact]
    public void Validate_WhenCollectionsPageSizeTooLarge_ShouldFail()
    {
        var request = new AnalyticsCollectionsRequest(
            _validFrom,
            _validTo,
            PageSize: AnalyticsConstants.MAX_COLLECTIONS_PAGE_SIZE + 1);

        ValidationResult result = _collectionsValidator.Validate(new GetSellerAnalyticsCollectionsQuery(_sellerId, request));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenCollectionsSortInvalidEnum_ShouldFail()
    {
        var request = new AnalyticsCollectionsRequest(_validFrom, _validTo);
        var query = new GetSellerAnalyticsCollectionsQuery(_sellerId, request with { Sort = (AnalyticsCollectionSort)999 });

        ValidationResult result = _collectionsValidator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER));
    }
}
