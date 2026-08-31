using AssetBlock.Application.UseCases.Auth.Login;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public class LoginCommandValidatorTests
{
    private readonly LoginCommandValidator _validator = new();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Validate_WhenEmailIsEmpty_ShouldFail(string email)
    {
        var command = new LoginCommand(email, "password123");
        ValidationResult result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Email");
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("test@")]
    [InlineData("@example.com")]
    public async Task Validate_WhenEmailIsInvalid_ShouldFail(string email)
    {
        var command = new LoginCommand(email, "password123");
        ValidationResult result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Email" && e.ErrorMessage.Contains("format"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Validate_WhenPasswordIsEmpty_ShouldFail(string password)
    {
        var command = new LoginCommand("test@example.com", password);
        ValidationResult result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Password");
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var command = new LoginCommand("test@example.com", "password123");
        ValidationResult result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
