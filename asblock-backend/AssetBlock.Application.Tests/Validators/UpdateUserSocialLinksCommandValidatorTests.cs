using AssetBlock.Application.UseCases.Users.UpdateSocialLinks;
using AssetBlock.Domain.Core.Dto.Users;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public class UpdateUserSocialLinksCommandValidatorTests
{
    private readonly UpdateUserSocialLinksCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenLinksNull_ShouldFail()
    {
        var cmd = new UpdateUserSocialLinksCommand(Guid.NewGuid(), null);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenDuplicatePlatform_ShouldFail()
    {
        var pid = Guid.NewGuid();
        var cmd = new UpdateUserSocialLinksCommand(
            Guid.NewGuid(),
            [
                new SocialLinkInput { PlatformId = pid, Url = "https://a.com" },
                new SocialLinkInput { PlatformId = pid, Url = "https://b.com" }
            ]);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenUrlInvalid_ShouldFail()
    {
        var cmd = new UpdateUserSocialLinksCommand(
            Guid.NewGuid(),
            [new SocialLinkInput { PlatformId = Guid.NewGuid(), Url = "ftp://bad" }]);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenValid_ShouldPass()
    {
        var cmd = new UpdateUserSocialLinksCommand(
            Guid.NewGuid(),
            [new SocialLinkInput { PlatformId = Guid.NewGuid(), Url = "https://github.com/u" }]);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenUrlIsHttp_ShouldPass()
    {
        var cmd = new UpdateUserSocialLinksCommand(
            Guid.NewGuid(),
            [new SocialLinkInput { PlatformId = Guid.NewGuid(), Url = "http://example.com/x" }]);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenUrlNotAbsolute_ShouldFail()
    {
        var cmd = new UpdateUserSocialLinksCommand(
            Guid.NewGuid(),
            [new SocialLinkInput { PlatformId = Guid.NewGuid(), Url = "/relative" }]);
        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
    }
}
