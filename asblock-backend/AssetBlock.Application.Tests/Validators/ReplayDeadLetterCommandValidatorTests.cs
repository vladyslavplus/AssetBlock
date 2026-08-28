using AssetBlock.Application.UseCases.Admin.Outbox.ReplayDeadLetter;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class ReplayDeadLetterCommandValidatorTests
{
    private readonly ReplayDeadLetterCommandValidator _validator = new();

    [Fact]
    public void Validate_WhenIdValid_ShouldNotHaveErrors()
    {
        var command = new ReplayDeadLetterCommand(Guid.NewGuid());
        var result = _validator.Validate(command);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenIdEmpty_ShouldHaveError()
    {
        var command = new ReplayDeadLetterCommand(Guid.Empty);
        var result = _validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Id");
    }
}
