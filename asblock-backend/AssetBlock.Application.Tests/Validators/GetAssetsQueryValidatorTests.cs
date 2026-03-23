using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Domain.Core.Dto.Assets;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class GetAssetsQueryValidatorTests
{
    private readonly GetAssetsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenSortByInvalid_ShouldFail()
    {
        var query = new GetAssetsQuery(new GetAssetsRequest { SortBy = "BadSort" });
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenMinPriceGreaterThanMax_ShouldFail()
    {
        var query = new GetAssetsQuery(new GetAssetsRequest { MinPrice = 10, MaxPrice = 5 });
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var query = new GetAssetsQuery(new GetAssetsRequest { SortBy = "Title", MinPrice = 1, MaxPrice = 9 });
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }
}
