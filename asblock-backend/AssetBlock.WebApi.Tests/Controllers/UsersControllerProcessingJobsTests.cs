using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.GetMyAssetProcessingJobs;
using AssetBlock.Application.UseCases.Assets.GetMyAssetVersionProcessingJobs;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
