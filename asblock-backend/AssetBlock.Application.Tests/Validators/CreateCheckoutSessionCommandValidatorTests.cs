using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class CreateCheckoutSessionCommandValidatorTests
{
    private readonly CreateCheckoutSessionCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenAssetIdEmpty_ShouldFail()
    {
        var cmd = new CreateCheckoutSessionCommand(Guid.Empty, Guid.NewGuid(), "https://ok.com/r", "https://ok.com/c");
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://insecure.com/ok")]
    [InlineData("not-a-url")]
    public async Task Validate_WhenUrlNotHttps_ShouldFail(string url)
    {
        var cmd = new CreateCheckoutSessionCommand(Guid.NewGuid(), Guid.NewGuid(), url, "https://cancel.com/");
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValidHttpsUrls_ShouldPass()
    {
        var cmd = new CreateCheckoutSessionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://example.com/success",
            "https://example.com/cancel");
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
