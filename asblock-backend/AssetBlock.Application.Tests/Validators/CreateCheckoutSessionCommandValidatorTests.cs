using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class CreateCheckoutSessionCommandValidatorTests
{
    private readonly CreateCheckoutSessionCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenAssetIdEmpty_ShouldFail()
    {
        var cmd = new CreateCheckoutSessionCommand(Guid.Empty, Guid.NewGuid());
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenUserIdEmpty_ShouldFail()
    {
        var cmd = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.Empty);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid());
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
