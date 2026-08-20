using AssetBlock.Application.UseCases.Bundles.GetBundles;
using AssetBlock.Domain.Core.Dto.Bundles;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetBundlesQueryValidatorTests
{
    private readonly GetBundlesQueryValidator _validator = new();

    private static GetBundlesQuery Valid(decimal? minPrice = null, decimal? maxPrice = null) =>
        new(new ListBundlesRequest { Page = 1, PageSize = 10, MinPrice = minPrice, MaxPrice = maxPrice });

    [Fact]
    public async Task Validate_WhenMinPriceGreaterThanMaxPrice_ShouldFail()
    {
        var result = await _validator.ValidateAsync(Valid(minPrice: 50m, maxPrice: 10m));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("MinPrice") && e.ErrorMessage.Contains("MaxPrice"));
    }

    [Fact]
    public async Task Validate_WhenMinPriceEqualsMaxPrice_ShouldPass()
    {
        var result = await _validator.ValidateAsync(Valid(minPrice: 10m, maxPrice: 10m));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenMinPriceLessThanMaxPrice_ShouldPass()
    {
        var result = await _validator.ValidateAsync(Valid(minPrice: 5m, maxPrice: 100m));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenOnlyMinPriceProvided_ShouldPass()
    {
        var result = await _validator.ValidateAsync(Valid(minPrice: 5m));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenOnlyMaxPriceProvided_ShouldPass()
    {
        var result = await _validator.ValidateAsync(Valid(maxPrice: 100m));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenNeitherMinNorMaxPriceProvided_ShouldPass()
    {
        var result = await _validator.ValidateAsync(Valid());

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    public async Task Validate_WhenMinPriceNegative_ShouldFail(double minPrice)
    {
        var result = await _validator.ValidateAsync(Valid(minPrice: (decimal)minPrice));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("MinPrice"));
    }
}
