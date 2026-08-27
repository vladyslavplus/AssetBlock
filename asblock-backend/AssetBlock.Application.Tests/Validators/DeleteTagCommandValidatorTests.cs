using AssetBlock.Application.UseCases.Tags.DeleteTag;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class DeleteTagCommandValidatorTests
{
    private readonly DeleteTagCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenValidId_ShouldPass()
    {
        var cmd = new DeleteTagCommand(Guid.NewGuid());
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenEmptyId_ShouldFail()
    {
        var cmd = new DeleteTagCommand(Guid.Empty);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(cmd.Id));
    }
}
