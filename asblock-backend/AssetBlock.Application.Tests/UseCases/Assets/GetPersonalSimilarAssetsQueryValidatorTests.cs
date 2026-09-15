using AssetBlock.Application.UseCases.Assets.GetPersonalSimilarAssets;
using AssetBlock.Domain.Core.Constants;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class GetPersonalSimilarAssetsQueryValidatorTests
{
    private readonly GetPersonalSimilarAssetsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenIdsEmpty_ShouldFail()
    {
        ValidationResult result = await _validator.ValidateAsync(new GetPersonalSimilarAssetsQuery(Guid.Empty, Guid.Empty));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task Validate_WhenLimitOutOfRange_ShouldFail(int limit)
    {
        ValidationResult result = await _validator.ValidateAsync(
            new GetPersonalSimilarAssetsQuery(Guid.NewGuid(), Guid.NewGuid(), limit));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenModeUnknown_ShouldFail()
    {
        ValidationResult result = await _validator.ValidateAsync(
            new GetPersonalSimilarAssetsQuery(Guid.NewGuid(), Guid.NewGuid(), 6, "personal"));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(SimilarAssetsConstants.MODE_SIMILARITY)]
    [InlineData(SimilarAssetsConstants.MODE_POPULARITY)]
    public async Task Validate_WhenValid_ShouldPass(string mode)
    {
        ValidationResult result = await _validator.ValidateAsync(
            new GetPersonalSimilarAssetsQuery(Guid.NewGuid(), Guid.NewGuid(), 6, mode));

        result.IsValid.Should().BeTrue();
    }
}
