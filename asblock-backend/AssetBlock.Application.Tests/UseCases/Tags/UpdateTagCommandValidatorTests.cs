using AssetBlock.Application.UseCases.Tags.UpdateTag;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class UpdateTagCommandValidatorTests
{
    private readonly UpdateTagCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenIdEmpty_ShouldFail()
    {
        var cmd = new UpdateTagCommand(Guid.Empty, "valid-name");
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new UpdateTagCommand(Guid.NewGuid(), "tools");
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
