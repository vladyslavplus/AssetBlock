using AssetBlock.Application.UseCases.Assets.RemoveAssetTag;
using AssetBlock.Application.Validators.Assets;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class RemoveAssetTagCommandValidatorTests
{
    private readonly RemoveAssetTagCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenTagIdEmpty_ShouldFail()
    {
        var cmd = new RemoveAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new RemoveAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
