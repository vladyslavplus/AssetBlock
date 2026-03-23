using AssetBlock.Application.UseCases.Assets.DeleteAsset;
using AssetBlock.Application.Validators.Assets;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class DeleteAssetCommandValidatorTests
{
    private readonly DeleteAssetCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenIdsEmpty_ShouldFail()
    {
        var cmd = new DeleteAssetCommand(Guid.Empty, Guid.Empty);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new DeleteAssetCommand(Guid.NewGuid(), Guid.NewGuid());
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
