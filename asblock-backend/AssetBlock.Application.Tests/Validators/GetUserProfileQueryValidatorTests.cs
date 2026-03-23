using AssetBlock.Application.UseCases.Users.GetProfile;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Validators;

public class GetUserProfileQueryValidatorTests
{
    private readonly GetUserProfileQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenUsernameBlankAndNoCurrentUser_ShouldFail()
    {
        var query = new GetUserProfileQuery("  ", null);
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenCurrentUserOnly_ShouldPass()
    {
        var query = new GetUserProfileQuery(null, Guid.NewGuid());
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenUsernameTooLong_ShouldFail()
    {
        var query = new GetUserProfileQuery(new string('a', 51), null);
        var result = await _validator.ValidateAsync(query);
        result.IsValid.Should().BeFalse();
    }
}
