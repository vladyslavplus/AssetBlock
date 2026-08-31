using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public class CreateCheckoutSessionCommandValidatorTests
{
    private readonly CreateCheckoutSessionCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenAssetIdEmpty_ShouldFail()
    {
        var cmd = new CreateCheckoutSessionCommand(Guid.Empty, Guid.NewGuid());
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenUserIdEmpty_ShouldFail()
    {
        var cmd = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.Empty);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
