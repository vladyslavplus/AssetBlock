using AssetBlock.Application.UseCases.Assets.GetSimilarAssets;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetSimilarAssetsQueryValidatorTests
{
    private readonly GetSimilarAssetsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenAssetIdEmpty_ShouldFail()
    {
        ValidationResult result = await _validator.ValidateAsync(new GetSimilarAssetsQuery(Guid.Empty));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "AssetId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    public async Task Validate_WhenLimitOutOfRange_ShouldFail(int limit)
    {
        ValidationResult result = await _validator.ValidateAsync(new GetSimilarAssetsQuery(Guid.NewGuid(), limit));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Limit");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public async Task Validate_WhenLimitInRange_ShouldPass(int limit)
    {
        ValidationResult result = await _validator.ValidateAsync(new GetSimilarAssetsQuery(Guid.NewGuid(), limit));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("similarity")]
    [InlineData("popularity")]
    public async Task Validate_WhenModeKnown_ShouldPass(string mode)
    {
        ValidationResult result = await _validator.ValidateAsync(new GetSimilarAssetsQuery(Guid.NewGuid(), 6, mode));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Similarity")]
    [InlineData("trending")]
    public async Task Validate_WhenModeUnknown_ShouldFail(string mode)
    {
        ValidationResult result = await _validator.ValidateAsync(new GetSimilarAssetsQuery(Guid.NewGuid(), 6, mode));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Mode");
    }
}
