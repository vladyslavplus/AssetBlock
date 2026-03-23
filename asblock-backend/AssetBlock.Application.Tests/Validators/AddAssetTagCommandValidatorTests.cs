using AssetBlock.Application.UseCases.Assets.AddAssetTag;
using AssetBlock.Application.Validators.Assets;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class AddAssetTagCommandValidatorTests
{
    private readonly AddAssetTagCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenTagNameInvalidPattern_ShouldFail()
    {
        var cmd = new AddAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), "Bad Tag!");
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new AddAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), "rust-game");
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
