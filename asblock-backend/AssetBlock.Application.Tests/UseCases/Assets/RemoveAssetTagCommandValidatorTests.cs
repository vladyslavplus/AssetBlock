using AssetBlock.Application.UseCases.Assets.RemoveAssetTag;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class RemoveAssetTagCommandValidatorTests
{
    private readonly RemoveAssetTagCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenTagIdEmpty_ShouldFail()
    {
        var cmd = new RemoveAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new RemoveAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
