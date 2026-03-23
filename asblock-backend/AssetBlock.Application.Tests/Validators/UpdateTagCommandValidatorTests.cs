using AssetBlock.Application.UseCases.Tags.UpdateTag;
using AssetBlock.Application.Validators.Tags;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class UpdateTagCommandValidatorTests
{
    private readonly UpdateTagCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenIdEmpty_ShouldFail()
    {
        var cmd = new UpdateTagCommand(Guid.Empty, "valid-name");
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new UpdateTagCommand(Guid.NewGuid(), "tools");
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
