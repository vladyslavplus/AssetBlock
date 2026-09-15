using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.GetSimilarAssets;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class SimilarAssetsControllerTests : ControllerTestBase
{
    private AssetsController CreateController()
    {
        return new AssetsController(
            Sender,
            Substitute.For<AssetBlock.Domain.Abstractions.Services.IDownloadService>(),
            Options.Create(new Domain.Core.Primitives.AppSettingsOptions.FileUploadOptions()),
            NullLogger<AssetsController>.Instance);
    }

    [Fact]
    public void GetSimilar_ShouldHaveCatalogSearchRateLimitingAttribute()
    {
        System.Reflection.MethodInfo? method = typeof(AssetsController).GetMethod(nameof(AssetsController.GetSimilar));
        EnableRateLimitingAttribute? attribute = method!.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .Cast<EnableRateLimitingAttribute>()
            .SingleOrDefault();

        attribute.Should().NotBeNull();
        attribute.PolicyName.Should().Be(RateLimitingConstants.Policies.CATALOG_SEARCH);
    }

    [Fact]
    public async Task GetSimilar_WhenSuccess_ShouldReturnOkPublicItems()
    {
        var item = new AssetListItem(
            Guid.NewGuid(),
            "Peer",
            null,
            5m,
            Guid.NewGuid(),
            "Tools",
            Guid.NewGuid(),
            "seller",
            DateTimeOffset.UtcNow,
            ["tag"],
            4.1);
        Sender.Send(Arg.Any<GetSimilarAssetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new SimilarAssetsResult([item], null, []))));

        IActionResult result = await CreateController().GetSimilar(Guid.NewGuid(), cancellationToken: CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        SimilarAssetsResult payload = ok.Value.Should().BeOfType<SimilarAssetsResult>().Subject;
        payload.Items.Should().ContainSingle().Which.Id.Should().Be(item.Id);
        payload.Explanations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSimilar_WhenHandlerReturnsExplanations_ShouldPassThemThrough()
    {
        var item = new AssetListItem(
            Guid.NewGuid(),
            "Peer",
            null,
            5m,
            Guid.NewGuid(),
            "Tools",
            Guid.NewGuid(),
            "seller",
            DateTimeOffset.UtcNow,
            ["tag"],
            4.1);
        var explanations = new List<SimilarAssetExplanation>
        {
            new(item.Id, SimilarAssetsExplanationCodes.SHARED_TAGS, "Shares 1 tag with this asset.")
        };
        Sender.Send(Arg.Any<GetSimilarAssetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new SimilarAssetsResult([item], null, explanations))));

        IActionResult result = await CreateController().GetSimilar(Guid.NewGuid(), cancellationToken: CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        SimilarAssetsResult payload = ok.Value.Should().BeOfType<SimilarAssetsResult>().Subject;
        payload.Explanations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(explanations[0]);
    }

    [Fact]
    public async Task GetSimilar_WhenEmpty_ShouldReturnOkEmptyItems()
    {
        Sender.Send(Arg.Any<GetSimilarAssetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new SimilarAssetsResult([], null, []))));

        IActionResult result = await CreateController().GetSimilar(Guid.NewGuid(), limit: 6, cancellationToken: CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)result).Value.Should().BeOfType<SimilarAssetsResult>().Subject.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSimilar_WhenSourceMissing_ShouldReturnNotFound()
    {
        Sender.Send(Arg.Any<GetSimilarAssetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SimilarAssetsResult>.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND)));

        AssetsController controller = CreateController();
        SetupAnonymous(controller);
        IActionResult result = await controller.GetSimilar(Guid.NewGuid(), cancellationToken: CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetSimilar_WhenPopularityMode_ShouldForwardModeToQuery()
    {
        GetSimilarAssetsQuery? captured = null;
        Sender.Send(Arg.Do<GetSimilarAssetsQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new SimilarAssetsResult([], null, []))));

        IActionResult result = await CreateController().GetSimilar(Guid.NewGuid(), 6, "popularity", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        captured.Should().NotBeNull();
        captured!.Mode.Should().Be("popularity");
    }
}
