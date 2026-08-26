using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.GetMyAssetProcessingJobs;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class GetMyAssetProcessingJobsQueryHandlerTests
{
    private readonly IAssetProcessingJobStore _jobStoreMock = Substitute.For<IAssetProcessingJobStore>();
    private readonly GetMyAssetProcessingJobsQueryHandler _handler;

    public GetMyAssetProcessingJobsQueryHandlerTests()
    {
        _handler = new GetMyAssetProcessingJobsQueryHandler(_jobStoreMock);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFoundOrInaccessible_ShouldReturnNotFound()
    {
        var assetId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var query = new GetMyAssetProcessingJobsQuery(assetId, ownerUserId);

        _jobStoreMock.GetJobsForAsset(assetId, ownerUserId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AssetProcessingJobDto>?)null);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
    }

    [Fact]
    public async Task Handle_WhenAssetOwnedWithZeroJobs_ShouldReturnSuccessWithEmptyList()
    {
        var assetId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var query = new GetMyAssetProcessingJobsQuery(assetId, ownerUserId);

        _jobStoreMock.GetJobsForAsset(assetId, ownerUserId, Arg.Any<CancellationToken>())
            .Returns(new List<AssetProcessingJobDto>());

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenAssetOwnedWithJobs_ShouldReturnSuccessWithJobs()
    {
        var assetId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var query = new GetMyAssetProcessingJobsQuery(assetId, ownerUserId);

        var expectedJobs = new List<AssetProcessingJobDto>
        {
            new(
                Guid.NewGuid(),
                assetId,
                versionId,
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

        _jobStoreMock.GetJobsForAsset(assetId, ownerUserId, Arg.Any<CancellationToken>())
            .Returns(expectedJobs);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Value.Should().HaveCount(1);
        result.Value[0].AssetId.Should().Be(assetId);
    }
}
