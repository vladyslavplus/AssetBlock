using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.GetMyAssetVersionProcessingJobs;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class GetMyAssetVersionProcessingJobsQueryHandlerTests
{
    private readonly IAssetProcessingJobStore _jobStoreMock = Substitute.For<IAssetProcessingJobStore>();
    private readonly GetMyAssetVersionProcessingJobsQueryHandler _handler;

    public GetMyAssetVersionProcessingJobsQueryHandlerTests()
    {
        _handler = new GetMyAssetVersionProcessingJobsQueryHandler(_jobStoreMock);
    }

    [Fact]
    public async Task Handle_WhenVersionNotFoundOrInaccessible_ShouldReturnNotFound()
    {
        var versionId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var query = new GetMyAssetVersionProcessingJobsQuery(versionId, ownerUserId);

        _jobStoreMock.GetJobsForVersion(versionId, ownerUserId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AssetProcessingJobDto>?)null);

        Result<IReadOnlyList<AssetProcessingJobDto>> result = await _handler.Handle(query, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
    }

    [Fact]
    public async Task Handle_WhenVersionOwnedWithZeroJobs_ShouldReturnSuccessWithEmptyList()
    {
        var versionId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var query = new GetMyAssetVersionProcessingJobsQuery(versionId, ownerUserId);

        _jobStoreMock.GetJobsForVersion(versionId, ownerUserId, Arg.Any<CancellationToken>())
            .Returns(new List<AssetProcessingJobDto>());

        Result<IReadOnlyList<AssetProcessingJobDto>> result = await _handler.Handle(query, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenVersionOwnedWithJobs_ShouldReturnSuccessWithJobs()
    {
        var assetId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var query = new GetMyAssetVersionProcessingJobsQuery(versionId, ownerUserId);

        var expectedJobs = new List<AssetProcessingJobDto>
        {
            new(
                Guid.NewGuid(),
                assetId,
                versionId,
                AssetProcessingJobType.MALWARE_SCAN,
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

        _jobStoreMock.GetJobsForVersion(versionId, ownerUserId, Arg.Any<CancellationToken>())
            .Returns(expectedJobs);

        Result<IReadOnlyList<AssetProcessingJobDto>> result = await _handler.Handle(query, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Value.Should().HaveCount(1);
        result.Value[0].AssetVersionId.Should().Be(versionId);
    }
}
