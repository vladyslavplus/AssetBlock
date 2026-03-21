using AssetBlock.Application.UseCases.Users.UpdateSocialLinks;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public class UpdateUserSocialLinksCommandHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly ISocialPlatformStore _platformStore = Substitute.For<ISocialPlatformStore>();
    private readonly UpdateUserSocialLinksCommandHandler _handler;

    public UpdateUserSocialLinksCommandHandlerTests()
    {
        _handler = new UpdateUserSocialLinksCommandHandler(_userStore, _platformStore, NullLogger<UpdateUserSocialLinksCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenPlatformUnknown_ShouldReturnNotFound()
    {
        var userId = Guid.NewGuid();
        var knownId = Guid.NewGuid();
        _platformStore.GetAll(Arg.Any<CancellationToken>())
            .Returns([new SocialPlatform { Id = knownId, Name = "A", IconName = "a" }]);

        var command = new UpdateUserSocialLinksCommand(userId,
        [
            new SocialLinkInput { PlatformId = Guid.NewGuid(), Url = "https://a.com/x" }
        ]);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_SOCIAL_PLATFORM_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenReplaceSucceeds_ShouldReturnLinks()
    {
        var userId = Guid.NewGuid();
        var platformId = Guid.NewGuid();
        _platformStore.GetAll(Arg.Any<CancellationToken>())
            .Returns([new SocialPlatform { Id = platformId, Name = "GitHub", IconName = "github" }]);
        _userStore.ReplaceUserSocialLinks(userId, Arg.Any<IReadOnlyList<(Guid PlatformId, string Url)>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var linkId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "u",
            Email = "u@u.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            SocialLinks =
            [
                new UserSocialLink
                {
                    Id = linkId,
                    UserId = userId,
                    PlatformId = platformId,
                    Platform = new SocialPlatform { Id = platformId, Name = "GitHub", IconName = "github" },
                    Url = "https://github.com/u"
                }
            ]
        };
        _userStore.GetByIdWithLinks(userId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(
            new UpdateUserSocialLinksCommand(userId, [new SocialLinkInput { PlatformId = platformId, Url = "https://github.com/u" }]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Url.Should().Be("https://github.com/u");
    }
}
