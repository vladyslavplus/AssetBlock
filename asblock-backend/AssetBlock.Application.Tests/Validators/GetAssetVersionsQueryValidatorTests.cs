using AssetBlock.Application.UseCases.Assets.GetAssetVersions;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetAssetVersionsQueryValidatorTests
{
    private readonly GetAssetVersionsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenValidQuery_ShouldPass()
    {
        var query = new GetAssetVersionsQuery(Guid.NewGuid(), Guid.NewGuid());
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenRequesterUserIdNull_ShouldPass()
    {
        var query = new GetAssetVersionsQuery(Guid.NewGuid(), null);
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenAssetIdEmpty_ShouldFail()
    {
        var query = new GetAssetVersionsQuery(Guid.Empty, null);
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(query.AssetId));
    }

    [Fact]
    public async Task Validate_WhenRequesterUserIdEmpty_ShouldFail()
    {
        var query = new GetAssetVersionsQuery(Guid.NewGuid(), Guid.Empty);
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(query.RequesterUserId));
    }
}
