using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.EnqueueListingCopilot;
using AssetBlock.Application.UseCases.Assets.GetListingCopilotSuggestion;
using AssetBlock.Application.UseCases.Assets.GetMyAssetProcessingJobs;
using AssetBlock.Application.UseCases.Assets.GetMyAssetVersionProcessingJobs;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class UsersControllerProcessingJobsTests : ControllerTestBase
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _assetId = Guid.NewGuid();
    private readonly Guid _versionId = Guid.NewGuid();

    [Fact]
    public async Task GetMyAssetProcessingJobs_WhenAnonymous_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);

        var result = await controller.GetMyAssetProcessingJobs(_assetId, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task GetMyAssetProcessingJobs_WhenNotFound_ShouldReturnNotFound()
    {
        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);

        Sender.Send(Arg.Any<GetMyAssetProcessingJobsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<AssetProcessingJobDto>>.NotFound("Asset was not found."));

        var result = await controller.GetMyAssetProcessingJobs(_assetId, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetMyAssetProcessingJobs_WhenSuccess_ShouldReturnOkWithJobs()
    {
        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);

        var jobs = new List<AssetProcessingJobDto>
        {
            new(
                Guid.NewGuid(),
                _assetId,
                _versionId,
                AssetProcessingJobType.ARCHIVE_INSPECTION,
                1,
                AssetProcessingJobStatus.SUCCEEDED,
                "SUCCEEDED",
                1,
                3,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)
        };

        Sender.Send(Arg.Any<GetMyAssetProcessingJobsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<AssetProcessingJobDto>>.Success(jobs));

        var result = await controller.GetMyAssetProcessingJobs(_assetId, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeAssignableTo<IReadOnlyList<AssetProcessingJobDto>>().Subject;
        returned.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetMyAssetVersionProcessingJobs_WhenAnonymous_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);

        var result = await controller.GetMyAssetVersionProcessingJobs(_versionId, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task GetMyAssetVersionProcessingJobs_WhenNotFound_ShouldReturnNotFound()
    {
        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);

        Sender.Send(Arg.Any<GetMyAssetVersionProcessingJobsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<AssetProcessingJobDto>>.NotFound("Asset version was not found."));

        var result = await controller.GetMyAssetVersionProcessingJobs(_versionId, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetMyAssetVersionProcessingJobs_WhenSuccess_ShouldReturnOkWithJobs()
    {
        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);

        var jobs = new List<AssetProcessingJobDto>
        {
            new(
                Guid.NewGuid(),
                _assetId,
                _versionId,
                AssetProcessingJobType.MALWARE_SCAN,
                1,
                AssetProcessingJobStatus.RUNNING,
                "RUNNING",
                1,
                3,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)
        };

        Sender.Send(Arg.Any<GetMyAssetVersionProcessingJobsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<AssetProcessingJobDto>>.Success(jobs));

        var result = await controller.GetMyAssetVersionProcessingJobs(_versionId, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeAssignableTo<IReadOnlyList<AssetProcessingJobDto>>().Subject;
        returned.Should().HaveCount(1);
    }

    [Fact]
    public void AssetProcessingJobDto_Serialization_ShouldProduceStringEnums()
    {
        var job = new AssetProcessingJobDto(
            Guid.NewGuid(),
            _assetId,
            _versionId,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            AssetProcessingJobStatus.RUNNING,
            "INSPECTING",
            1,
            3,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var json = System.Text.Json.JsonSerializer.Serialize(job);

        json.Should().Contain("\"Type\":\"ARCHIVE_INSPECTION\"");
        json.Should().Contain("\"Status\":\"RUNNING\"");
    }
}

public sealed class UsersControllerListingCopilotTests : ControllerTestBase
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _versionId = Guid.NewGuid();

    [Fact]
    public async Task EnqueueListingCopilot_WhenAnonymous_ShouldReturnUnauthorized()
    {
        var controller = new UsersController(Sender);
        SetupAnonymous(controller);

        var result = await controller.EnqueueListingCopilot(_versionId, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task EnqueueListingCopilot_WhenSuccess_ShouldReturnAccepted()
    {
        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        var payload = new ListingCopilotEnqueueResponse(Guid.NewGuid(), _versionId);
        Sender.Send(Arg.Any<EnqueueListingCopilotCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ListingCopilotEnqueueResponse>.Success(payload));

        var result = await controller.EnqueueListingCopilot(_versionId, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.Value.Should().Be(payload);
    }

    [Fact]
    public async Task GetListingCopilotSuggestion_WhenNotFound_ShouldReturnNotFound()
    {
        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        Sender.Send(Arg.Any<GetListingCopilotSuggestionQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<ListingCopilotSuggestionDto>.NotFound());

        var result = await controller.GetListingCopilotSuggestion(_versionId, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task EnqueueListingCopilot_WhenConflict_ShouldReturnConflict()
    {
        var controller = new UsersController(Sender);
        SetupUser(_userId, controller);
        Sender.Send(Arg.Any<EnqueueListingCopilotCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ListingCopilotEnqueueResponse>.Conflict(ErrorCodes.ERR_AI_VERSION_NOT_READY));

        var result = await controller.EnqueueListingCopilot(_versionId, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status409Conflict);
    }

    [Fact]
    public void EnqueueListingCopilot_ShouldUseDedicatedRateLimitPolicy()
    {
        var method = typeof(UsersController).GetMethod(nameof(UsersController.EnqueueListingCopilot));
        var attribute = method!
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .Cast<EnableRateLimitingAttribute>()
            .Single();

        attribute.PolicyName.Should().Be(RateLimitingConstants.Policies.LISTING_COPILOT_ENQUEUE);
    }
}
