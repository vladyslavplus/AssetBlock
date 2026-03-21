using AssetBlock.Application.UseCases.Users.GetProfile;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public class GetUserProfileQueryHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly GetUserProfileQueryHandler _handler;

    public GetUserProfileQueryHandlerTests()
    {
        _handler = new GetUserProfileQueryHandler(_userStore);
    }

    [Fact]
    public async Task Handle_WhenUsernameNotFound_ShouldReturnNotFound()
    {
        var q = new GetUserProfileQuery("nobody", null);
        _userStore.GetByUsernameWithLinks("nobody", Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(q, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_USER_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenPrivateAndNotOwner_ShouldReturnNotFound()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var user = new User
        {
            Id = ownerId,
            Username = "a",
            Email = "a@a.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            IsPublicProfile = false,
            SocialLinks = []
        };
        _userStore.GetByUsernameWithLinks("a", Arg.Any<CancellationToken>()).Returns(user);

        var q = new GetUserProfileQuery("a", viewerId);
        var result = await _handler.Handle(q, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_USER_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenPrivateAndOwner_ShouldReturnProfile()
    {
        var ownerId = Guid.NewGuid();
        var platform = new SocialPlatform { Id = Guid.NewGuid(), Name = "GitHub", IconName = "github" };
        var user = new User
        {
            Id = ownerId,
            Username = "a",
            Email = "a@a.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            IsPublicProfile = false,
            SocialLinks =
            [
                new UserSocialLink
                {
                    Id = Guid.NewGuid(),
                    UserId = ownerId,
                    PlatformId = platform.Id,
                    Platform = platform,
                    Url = "https://github.com/a"
                }
            ]
        };
        _userStore.GetByUsernameWithLinks("a", Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new GetUserProfileQuery("a", ownerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Username.Should().Be("a");
        result.Value.SocialLinks.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_WhenMePath_ShouldReturnProfile()
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = "meuser",
            Email = "m@a.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            SocialLinks = []
        };
        _userStore.GetByIdWithLinks(id, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new GetUserProfileQuery(null, id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Username.Should().Be("meuser");
    }
}
