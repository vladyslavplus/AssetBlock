using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.GetPersonalSimilarAssets;
using AssetBlock.Application.UseCases.Users.GetRecommendationPreferences;
using AssetBlock.Application.UseCases.Users.UpdateRecommendationPreferences;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class PersonalSimilarAssetsControllerTests : ControllerTestBase
{
    private readonly Guid _userId = Guid.NewGuid();

    private UsersController CreateController() => new(Sender);

    [Fact]
    public async Task GetPersonalSimilar_WhenNoUser_ShouldReturnUnauthorized()
    {
        UsersController controller = CreateController();
        SetupAnonymous(controller);
        IActionResult result = await controller.GetPersonalSimilar(Guid.NewGuid(), 6, "similarity", CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task GetPersonalSimilar_WhenAuthenticated_ShouldForwardUserIdAndReturnOk()
    {
        GetPersonalSimilarAssetsQuery? captured = null;
        Sender.Send(Arg.Do<GetPersonalSimilarAssetsQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new SimilarAssetsResult([], null, []))));

        UsersController controller = CreateController();
        SetupUser(_userId, controller);
        var assetId = Guid.NewGuid();
        IActionResult result = await controller.GetPersonalSimilar(assetId, 6, "similarity", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        captured.Should().NotBeNull();
        captured!.AssetId.Should().Be(assetId);
        captured.UserId.Should().Be(_userId);
        captured.Limit.Should().Be(6);
    }

    [Fact]
    public void GetPersonalSimilar_ShouldRequireAuthorizationAndCatalogSearchRateLimit()
    {
        System.Reflection.MethodInfo? method = typeof(UsersController).GetMethod(nameof(UsersController.GetPersonalSimilar));
        method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Should().ContainSingle();
        EnableRateLimitingAttribute? rateLimit = method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .Cast<EnableRateLimitingAttribute>()
            .SingleOrDefault();
        rateLimit.Should().NotBeNull();
        rateLimit.PolicyName.Should().Be(Domain.Core.Constants.RateLimitingConstants.Policies.CATALOG_SEARCH);
    }

    [Fact]
    public async Task GetRecommendationPreferences_WhenNoUser_ShouldReturnUnauthorized()
    {
        UsersController controller = CreateController();
        SetupAnonymous(controller);
        IActionResult result = await controller.GetRecommendationPreferences(CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task GetRecommendationPreferences_WhenAuthenticated_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetRecommendationPreferencesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new RecommendationPreferencesDto { IsPersonalized = true, OptedInAt = DateTimeOffset.UtcNow })));

        UsersController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.GetRecommendationPreferences(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateRecommendationPreferences_WhenNoUser_ShouldReturnUnauthorized()
    {
        UsersController controller = CreateController();
        SetupAnonymous(controller);
        IActionResult result = await controller.UpdateRecommendationPreferences(
            new UpdateRecommendationPreferencesRequest { IsPersonalized = true },
            CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task UpdateRecommendationPreferences_WhenAuthenticated_ShouldForwardUserIdAndReturnOk()
    {
        UpdateRecommendationPreferencesCommand? captured = null;
        Sender.Send(Arg.Do<UpdateRecommendationPreferencesCommand>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new RecommendationPreferencesDto { IsPersonalized = false, OptedInAt = null })));

        UsersController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.UpdateRecommendationPreferences(
            new UpdateRecommendationPreferencesRequest { IsPersonalized = false },
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        captured.Should().NotBeNull();
        captured!.UserId.Should().Be(_userId);
        captured.IsPersonalized.Should().BeFalse();
    }
}
