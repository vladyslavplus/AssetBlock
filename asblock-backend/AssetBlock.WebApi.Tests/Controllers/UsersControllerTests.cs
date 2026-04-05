using AssetBlock.Application.UseCases.Users.GetProfile;
using AssetBlock.Application.UseCases.Users.ListNotifications;
using AssetBlock.Application.UseCases.Users.ListSocialPlatforms;
using AssetBlock.Application.UseCases.Users.MarkNotificationRead;
using AssetBlock.Application.UseCases.Users.UpdateProfile;
using AssetBlock.Application.UseCases.Users.UpdateSocialLinks;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NoValueResult = Ardalis.Result.Result;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class UsersControllerTests : ControllerTestBase
{
    private readonly Guid _userId = Guid.NewGuid();

    [Fact]
    public async Task ListSocialPlatforms_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<ListSocialPlatformsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new List<SocialPlatformListItemDto>())));

        var controller = new UsersController(Sender);
        var result = await controller.ListSocialPlatforms(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ListMyNotifications_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        var result = await controller.ListMyNotifications(new GetNotificationsRequest(), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task ListMyNotifications_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new DomainPaging.PagedResult<NotificationListItemDto>([], 0, 1, 10))));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        var action = await controller.ListMyNotifications(new GetNotificationsRequest(), CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task MarkMyNotificationRead_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        var result = await controller.MarkMyNotificationRead(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task MarkMyNotificationRead_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<MarkNotificationReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        var action = await controller.MarkMyNotificationRead(Guid.NewGuid(), CancellationToken.None);

        action.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task GetMe_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        var result = await controller.GetMe(CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task GetMe_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetUserProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new UserProfileDto
            {
                Id = Guid.NewGuid(),
                Username = "u",
                AvatarUrl = null,
                Bio = null,
                IsPublicProfile = true,
                CreatedAt = DateTimeOffset.UtcNow,
                SocialLinks = []
            })));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        var action = await controller.GetMe(CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateMe_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        var result = await controller.UpdateMe(new UpdateUserProfileRequest(), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task UpdateMe_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<UpdateUserProfileCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new UpdateUserProfileResponse { Username = "x", IsPublicProfile = true })));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        var action = await controller.UpdateMe(new UpdateUserProfileRequest { Username = "x" }, CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateSocials_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        var pid = Guid.NewGuid();
        var result = await controller.UpdateSocials(new UpdateUserSocialLinksRequest
        {
            Links = [new SocialLinkInput { PlatformId = pid, Url = "https://x.com" }]
        }, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task UpdateSocials_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<UpdateUserSocialLinksCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new List<UserSocialLinkDto>())));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        var pid = Guid.NewGuid();
        var action = await controller.UpdateSocials(new UpdateUserSocialLinksRequest
        {
            Links = [new SocialLinkInput { PlatformId = pid, Url = "https://x.com" }]
        }, CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetByUsername_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetUserProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new UserProfileDto
            {
                Id = Guid.NewGuid(),
                Username = "u",
                AvatarUrl = null,
                Bio = null,
                IsPublicProfile = true,
                CreatedAt = DateTimeOffset.UtcNow,
                SocialLinks = []
            })));

        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        var result = await controller.GetByUsername("name", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
