using AssetBlock.Application.UseCases.Assets.GetAssetVersions;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class GetAssetVersionsQueryHandlerTests
{
    private readonly IAssetStore _assetStoreMock = Substitute.For<IAssetStore>();
    private readonly GetAssetVersionsQueryHandler _handler;

    public GetAssetVersionsQueryHandlerTests()
    {
        _handler = new GetAssetVersionsQueryHandler(_assetStoreMock);
    }

    [Fact]
    public async Task Handle_WhenVersionsFound_ShouldReturnSuccessWithVersionList()
    {
        var assetId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();
        var versions = new List<AssetVersionSummaryDto>
        {
            new(
                Guid.NewGuid(),
                1,
                true,
                "file.zip",
                1024,
                "sha256",
                "Initial release",
                DateTimeOffset.UtcNow,
                new AssetLicenseSummaryDto("standard", "Standard", "1.0", "terms"))
        };

        _assetStoreMock.ListVersions(assetId, requesterUserId, Arg.Any<CancellationToken>())
            .Returns(versions);

        var query = new GetAssetVersionsQuery(assetId, requesterUserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].FileName.Should().Be("file.zip");
    }

    [Fact]
    public async Task Handle_WhenStoreReturnsNull_ShouldReturnNotFound()
    {
        var assetId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();

        _assetStoreMock.ListVersions(assetId, requesterUserId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AssetVersionSummaryDto>?)null);

        var query = new GetAssetVersionsQuery(assetId, requesterUserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }
}
