using AssetBlock.Application.UseCases.Assets.UpdateAsset;
using AssetBlock.Application.Validators.Assets;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class UpdateAssetCommandValidatorTests
{
    private readonly UpdateAssetCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenNoFieldsProvided_ShouldFail()
    {
        var cmd = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenTitleEmptyString_ShouldFail()
    {
        var cmd = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), "", null, null, null);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenPriceZero_ShouldFail()
    {
        var cmd = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), null, null, 0, null);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenTitleValid_ShouldPass()
    {
        var cmd = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), "New title", null, null, null);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
