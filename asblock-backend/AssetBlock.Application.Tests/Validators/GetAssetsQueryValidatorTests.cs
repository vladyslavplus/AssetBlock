using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Domain.Core.Dto.Assets;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public class GetAssetsQueryValidatorTests
{
    private readonly GetAssetsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenSortByInvalid_ShouldFail()
    {
        var query = new GetAssetsQuery(new GetAssetsRequest { SortBy = "BadSort" });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenMinPriceGreaterThanMax_ShouldFail()
    {
        var query = new GetAssetsQuery(new GetAssetsRequest { MinPrice = 10, MaxPrice = 5 });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var query = new GetAssetsQuery(new GetAssetsRequest { SortBy = "Title", MinPrice = 1, MaxPrice = 9 });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenSearchExceeds256Scalars_ShouldFail()
    {
        var query = new GetAssetsQuery(new GetAssetsRequest { Search = new string('s', 257) });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("256 Unicode scalars"));
    }

    [Fact]
    public async Task Validate_WhenSearchContainsInvalidControlCharacter_ShouldFail()
    {
        var query = new GetAssetsQuery(new GetAssetsRequest { Search = "invalid\0query" });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("invalid control characters"));
    }

    [Fact]
    public async Task Validate_WhenSearchValidWithTabsOrNewlines_ShouldPass()
    {
        var query = new GetAssetsQuery(new GetAssetsRequest { Search = "valid\tsearch\nquery" });
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }
}
