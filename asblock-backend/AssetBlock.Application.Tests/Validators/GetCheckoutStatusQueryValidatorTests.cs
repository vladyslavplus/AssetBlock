using AssetBlock.Application.UseCases.Payments.GetCheckoutStatus;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public sealed class GetCheckoutStatusQueryValidatorTests
{
    private readonly GetCheckoutStatusQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenValidQuery_ShouldPass()
    {
        var query = new GetCheckoutStatusQuery(Guid.NewGuid(), Guid.NewGuid());
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenEmptyCheckoutIntentId_ShouldFail()
    {
        var query = new GetCheckoutStatusQuery(Guid.Empty, Guid.NewGuid());
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(query.CheckoutIntentId));
    }

    [Fact]
    public async Task Validate_WhenEmptyUserId_ShouldFail()
    {
        var query = new GetCheckoutStatusQuery(Guid.NewGuid(), Guid.Empty);
        ValidationResult result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(query.UserId));
    }
}
