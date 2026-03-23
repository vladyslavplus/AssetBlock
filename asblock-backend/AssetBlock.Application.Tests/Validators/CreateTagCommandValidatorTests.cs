using AssetBlock.Application.UseCases.Tags.CreateTag;
using AssetBlock.Application.Validators.Tags;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class CreateTagCommandValidatorTests
{
    private readonly CreateTagCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenNameInvalid_ShouldFail()
    {
        var cmd = new CreateTagCommand("Invalid_Name");
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new CreateTagCommand("game-assets");
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
