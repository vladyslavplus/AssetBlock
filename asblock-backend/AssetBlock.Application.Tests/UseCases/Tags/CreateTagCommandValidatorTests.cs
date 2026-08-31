using AssetBlock.Application.UseCases.Tags.CreateTag;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class CreateTagCommandValidatorTests
{
    private readonly CreateTagCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenNameInvalid_ShouldFail()
    {
        var cmd = new CreateTagCommand("Invalid_Name");
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new CreateTagCommand("game-assets");
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
