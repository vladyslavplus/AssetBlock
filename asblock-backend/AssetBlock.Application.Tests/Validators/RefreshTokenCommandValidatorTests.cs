using AssetBlock.Application.UseCases.Auth.RefreshToken;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class RefreshTokenCommandValidatorTests
{
    private readonly RefreshTokenCommandValidator _validator = new();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Validate_WhenRefreshTokenIsEmpty_ShouldFail(string token)
    {
        var command = new RefreshTokenCommand(token);
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "RefreshToken");
    }

    [Fact]
    public async Task Validate_WhenRefreshTokenIsTooLong_ShouldFail()
    {
        var token = new string('x', 2001);
        var command = new RefreshTokenCommand(token);
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "RefreshToken");
    }

    [Fact]
    public async Task Validate_WhenTokenIsValid_ShouldPass()
    {
        var command = new RefreshTokenCommand("valid-token");
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
