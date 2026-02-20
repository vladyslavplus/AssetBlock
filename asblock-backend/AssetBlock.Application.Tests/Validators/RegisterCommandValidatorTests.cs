using AssetBlock.Application.UseCases.Auth.Register;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class RegisterCommandValidatorTests
{
    private readonly RegisterCommandValidator _validator = new();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Validate_WhenEmailIsEmpty_ShouldFail(string email)
    {
        var command = new RegisterCommand(email, "password123");
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Email");
    }

    [Theory]
    [InlineData("invalid-email")]
    [InlineData("test@")]
    [InlineData("@example.com")]
    public async Task Validate_WhenEmailIsInvalid_ShouldFail(string email)
    {
        var command = new RegisterCommand(email, "password123");
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Email" && e.ErrorMessage.Contains("format"));
    }

    [Fact]
    public async Task Validate_WhenEmailIsTooLong_ShouldFail()
    {
        var email = new string('a', 250) + "@example.com";
        var command = new RegisterCommand(email, "password123");
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Email" && e.ErrorMessage.Contains("exceed 256 characters"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Validate_WhenPasswordIsEmpty_ShouldFail(string password)
    {
        var command = new RegisterCommand("test@example.com", password);
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Password");
    }

    [Fact]
    public async Task Validate_WhenPasswordIsTooShort_ShouldFail()
    {
        var command = new RegisterCommand("test@example.com", "short");
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Password" && e.ErrorMessage.Contains("at least 8 characters"));
    }

    [Fact]
    public async Task Validate_WhenPasswordIsTooLong_ShouldFail()
    {
        var password = new string('a', 501);
        var command = new RegisterCommand("test@example.com", password);
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Password" && e.ErrorMessage.Contains("exceed 500 characters"));
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var command = new RegisterCommand("test@example.com", "valid_password!");
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
