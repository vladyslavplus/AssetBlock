using AssetBlock.Application.UseCases.Assets.GetSellerAssetDetail;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetSellerAssetDetailQueryValidatorTests
{
    private readonly GetSellerAssetDetailQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenAssetIdIsEmpty_ShouldFail()
    {
        var query = new GetSellerAssetDetailQuery(Guid.Empty, Guid.NewGuid());
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "AssetId");
    }

    [Fact]
    public async Task Validate_WhenOwnerUserIdIsEmpty_ShouldFail()
    {
        var query = new GetSellerAssetDetailQuery(Guid.NewGuid(), Guid.Empty);
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "OwnerUserId");
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var query = new GetSellerAssetDetailQuery(Guid.NewGuid(), Guid.NewGuid());
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }
}
