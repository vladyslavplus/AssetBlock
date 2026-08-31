using AssetBlock.Application.UseCases.Users.UpdateProfile;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public class UpdateUserProfileCommandValidatorTests
{
    private readonly UpdateUserProfileCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenUsernameWhitespace_ShouldFail()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), "   ", null, null, null);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenAvatarUrlTooLong_ShouldFail()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), null, new string('x', 501), null, null);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenBioTooLong_ShouldFail()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), null, null, new string('b', 1001), null);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenAvatarUrlJavascriptScheme_ShouldFail()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), null, "javascript:alert(1)", null, null);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenAvatarUrlRelative_ShouldFail()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), null, "/images/avatar.png", null, null);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenAvatarUrlHttpsValid_ShouldPass()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), null, "https://cdn.example.com/avatar.png", null, null);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenAvatarUrlEmpty_ShouldPass()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), null, "", null, null);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenAvatarUrlWhitespace_ShouldPass()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), null, "   ", null, null);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }
}
