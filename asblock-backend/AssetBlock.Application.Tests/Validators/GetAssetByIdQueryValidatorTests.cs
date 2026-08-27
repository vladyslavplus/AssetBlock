using AssetBlock.Application.UseCases.Assets.GetAssetById;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetAssetByIdQueryValidatorTests
{
    private readonly GetAssetByIdQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenValidId_ShouldPass()
    {
        var query = new GetAssetByIdQuery(Guid.NewGuid());
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenEmptyId_ShouldFail()
    {
        var query = new GetAssetByIdQuery(Guid.Empty);
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(query.Id));
    }
}
