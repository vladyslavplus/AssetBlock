using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.GetSellerAssetDetail;
using AssetBlock.Application.UseCases.Users.GetMyListings;
using AssetBlock.Application.UseCases.Users.GetProfile;
using AssetBlock.Application.UseCases.Users.ListNotifications;
using AssetBlock.Application.UseCases.Users.ListSocialPlatforms;
using AssetBlock.Application.UseCases.Users.MarkNotificationRead;
using AssetBlock.Application.UseCases.Users.UpdateProfile;
using AssetBlock.Application.UseCases.Users.UpdateSocialLinks;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
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
        IActionResult result = await controller.ListSocialPlatforms(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ListMyNotifications_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        IActionResult result = await controller.ListMyNotifications(new GetNotificationsRequest(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ListMyNotifications_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new DomainPaging.PagedResult<NotificationListItemDto>([], 0, 1, 10))));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        IActionResult action = await controller.ListMyNotifications(new GetNotificationsRequest(), CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ListMyAssets_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        IActionResult result = await controller.ListMyAssets(new GetAssetsRequest(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ListMyAssets_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetMyListingsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new DomainPaging.PagedResult<SellerAssetListItem>([], 0, 1, 10))));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        IActionResult action = await controller.ListMyAssets(new GetAssetsRequest(), CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task MarkMyNotificationRead_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        IActionResult result = await controller.MarkMyNotificationRead(Guid.NewGuid(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task MarkMyNotificationRead_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<MarkNotificationReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        IActionResult action = await controller.MarkMyNotificationRead(Guid.NewGuid(), CancellationToken.None);

        action.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task GetMe_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        IActionResult result = await controller.GetMe(CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task GetMe_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetUserProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new UserProfileDto
            {
                Id = Guid.NewGuid(),
                Username = "u",
                Email = null,
                AvatarUrl = null,
                Bio = null,
                IsPublicProfile = true,
                CreatedAt = DateTimeOffset.UtcNow,
                SocialLinks = [],
                Role = null
            })));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        IActionResult action = await controller.GetMe(CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateMe_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        IActionResult result = await controller.UpdateMe(new UpdateUserProfileRequest(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task UpdateMe_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<UpdateUserProfileCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new UpdateUserProfileResponse { Username = "x", IsPublicProfile = true })));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        IActionResult action = await controller.UpdateMe(new UpdateUserProfileRequest { Username = "x" }, CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateSocials_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        var pid = Guid.NewGuid();
        IActionResult result = await controller.UpdateSocials(new UpdateUserSocialLinksRequest
        {
            Links = [new SocialLinkInput { PlatformId = pid, Url = "https://x.com" }]
        }, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task UpdateSocials_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<UpdateUserSocialLinksCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new List<UserSocialLinkDto>())));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        var pid = Guid.NewGuid();
        IActionResult action = await controller.UpdateSocials(new UpdateUserSocialLinksRequest
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
                Email = null,
                AvatarUrl = null,
                Bio = null,
                IsPublicProfile = true,
                CreatedAt = DateTimeOffset.UtcNow,
                SocialLinks = [],
                Role = null
            })));

        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        IActionResult result = await controller.GetByUsername("name", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetMyAsset_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);
        IActionResult result = await controller.GetMyAsset(Guid.NewGuid(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task GetMyAsset_WhenNotFound_ShouldReturnNotFound()
    {
        Sender.Send(Arg.Any<GetSellerAssetDetailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<SellerAssetDetailItem>.NotFound("ERR_ASSET_NOT_FOUND"));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        IActionResult result = await controller.GetMyAsset(Guid.NewGuid(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetMyAsset_WhenAuthenticated_ShouldReturnOk()
    {
        var assetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var detail = new SellerAssetDetailItem(
            assetId,
            "Pending Pack",
            null,
            9.99m,
            Guid.NewGuid(),
            "3D",
            _userId,
            "seller",
            now,
            null,
            [],
            Guid.NewGuid(),
            1,
            null,
            Domain.Core.Enums.AssetVersionProcessingStatus.PENDING_INSPECTION,
            now,
            null,
            null);

        Sender.Send(Arg.Any<GetSellerAssetDetailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(detail));

        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        IActionResult action = await controller.GetMyAsset(assetId, CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>();
    }
}
