using AssetBlock.Application.UseCases.Users.UpdateProfile;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class UpdateUserProfileCommandValidatorTests
{
    private readonly UpdateUserProfileCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenUsernameWhitespace_ShouldFail()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), "   ", null, null, null);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenAvatarUrlTooLong_ShouldFail()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), null, new string('x', 501), null, null);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenBioTooLong_ShouldFail()
    {
        var cmd = new UpdateUserProfileCommand(Guid.NewGuid(), null, null, new string('b', 1001), null);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }
}
