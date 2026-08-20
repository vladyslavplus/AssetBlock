using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsOverview;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsProducts;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsSales;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class SellerAnalyticsQueryValidatorTests
{
    private static readonly Guid _sellerId = Guid.NewGuid();
    private static readonly DateOnly _validFrom = new(2024, 1, 1);
    private static readonly DateOnly _validTo = new(2024, 1, 11);


    private readonly GetSellerAnalyticsOverviewQueryValidator _overviewValidator = new();

    [Fact]
    public async Task Overview_ValidQuery_PassesValidation()
    {
        var query = new GetSellerAnalyticsOverviewQuery(_sellerId, _validFrom, _validTo);
        var result = await _overviewValidator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Overview_ToBeforeFrom_FailsValidation()
    {
        var query = new GetSellerAnalyticsOverviewQuery(_sellerId, _validTo, _validFrom);
        var result = await _overviewValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Overview_SameFromTo_FailsValidation()
    {
        var query = new GetSellerAnalyticsOverviewQuery(_sellerId, _validFrom, _validFrom);
        var result = await _overviewValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Overview_RangeExceeds366Days_FailsValidation()
    {
        var from = new DateOnly(2024, 1, 1);
        var to = from.AddDays(AnalyticsConstants.MAX_DAYS + 1);
        var query = new GetSellerAnalyticsOverviewQuery(_sellerId, from, to);
        var result = await _overviewValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Overview_ToAfterTomorrowUtc_FailsValidation()
    {
        var to = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var query = new GetSellerAnalyticsOverviewQuery(_sellerId, _validFrom, to);
        var result = await _overviewValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }


    [Fact]
    public async Task Overview_ComparisonPeriodNotRepresentable_FailsValidation()
    {
        var from = DateOnly.MinValue.AddDays(1);
        var to = from.AddDays(10);
        var query = new GetSellerAnalyticsOverviewQuery(_sellerId, from, to);
        var result = await _overviewValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE));
    }


    private readonly GetSellerAnalyticsProductsQueryValidator _productsValidator = new();

    [Fact]
    public async Task Products_ValidQuery_PassesValidation()
    {
        var req = new AnalyticsProductsRequest(_validFrom, _validTo);
        var query = new GetSellerAnalyticsProductsQuery(_sellerId, req);
        var result = await _productsValidator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Products_RatingSortWithBundleFilter_FailsValidation()
    {
        var req = new AnalyticsProductsRequest(_validFrom, _validTo,
            ProductType: AnalyticsProductTypeFilter.BUNDLE,
            Sort: AnalyticsProductSort.RATING);
        var query = new GetSellerAnalyticsProductsQuery(_sellerId, req);
        var result = await _productsValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Products_InvalidPageSize_FailsValidation()
    {
        var req = new AnalyticsProductsRequest(_validFrom, _validTo, PageSize: AnalyticsConstants.MAX_PRODUCTS_PAGE_SIZE + 1);
        var query = new GetSellerAnalyticsProductsQuery(_sellerId, req);
        var result = await _productsValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Products_PageZero_FailsValidation()
    {
        var req = new AnalyticsProductsRequest(_validFrom, _validTo, Page: 0);
        var query = new GetSellerAnalyticsProductsQuery(_sellerId, req);
        var result = await _productsValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Products_PageIntMaxValue_FailsValidation()
    {
        var req = new AnalyticsProductsRequest(_validFrom, _validTo, Page: int.MaxValue);
        var query = new GetSellerAnalyticsProductsQuery(_sellerId, req);
        var result = await _productsValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER));
    }

    [Fact]
    public async Task Products_PageExceedsMaxPage_FailsValidation()
    {
        var req = new AnalyticsProductsRequest(_validFrom, _validTo, Page: AnalyticsConstants.MAX_PRODUCTS_PAGE + 1);
        var query = new GetSellerAnalyticsProductsQuery(_sellerId, req);
        var result = await _productsValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER));
    }

    [Fact]
    public async Task Products_OffsetExceedsMaxOffset_FailsValidation()
    {
        var req = new AnalyticsProductsRequest(
            _validFrom,
            _validTo,
            Page: AnalyticsConstants.MAX_PRODUCTS_PAGE,
            PageSize: AnalyticsConstants.MAX_PRODUCTS_PAGE_SIZE);
        var query = new GetSellerAnalyticsProductsQuery(_sellerId, req);
        var result = await _productsValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER));
    }


    private readonly GetSellerAnalyticsSalesQueryValidator _salesValidator = new();

    [Fact]
    public async Task Sales_ValidQuery_PassesValidation()
    {
        var req = new AnalyticsSalesRequest(_validFrom, _validTo);
        var query = new GetSellerAnalyticsSalesQuery(_sellerId, req);
        var result = await _salesValidator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Sales_OversizedCursor_FailsValidation()
    {
        var oversized = new string('A', AnalyticsConstants.MAX_CURSOR_LENGTH + 1);
        var req = new AnalyticsSalesRequest(_validFrom, _validTo, Cursor: oversized);
        var query = new GetSellerAnalyticsSalesQuery(_sellerId, req);
        var result = await _salesValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Sales_InvalidCursor_FailsValidation()
    {
        var req = new AnalyticsSalesRequest(_validFrom, _validTo, Cursor: "not-valid-base64-at-all!!!");
        var query = new GetSellerAnalyticsSalesQuery(_sellerId, req);
        var result = await _salesValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Sales_ValidCursor_PassesValidation()
    {
        var cursor = SalesCursorCodec
            .Encode(DateTimeOffset.UtcNow, Guid.NewGuid());

        var req = new AnalyticsSalesRequest(_validFrom, _validTo, Cursor: cursor);
        var query = new GetSellerAnalyticsSalesQuery(_sellerId, req);
        var result = await _salesValidator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Sales_NullCursor_PassesValidation()
    {
        var req = new AnalyticsSalesRequest(_validFrom, _validTo, Cursor: null);
        var query = new GetSellerAnalyticsSalesQuery(_sellerId, req);
        var result = await _salesValidator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Sales_InvalidPageSize_FailsValidation()
    {
        var req = new AnalyticsSalesRequest(_validFrom, _validTo, PageSize: AnalyticsConstants.MAX_SALES_PAGE_SIZE + 1);
        var query = new GetSellerAnalyticsSalesQuery(_sellerId, req);
        var result = await _salesValidator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }
}
