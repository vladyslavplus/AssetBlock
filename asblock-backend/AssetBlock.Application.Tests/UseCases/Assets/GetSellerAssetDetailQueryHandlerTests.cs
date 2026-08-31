using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.GetSellerAssetDetail;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class GetSellerAssetDetailQueryHandlerTests
{
    private readonly IAssetStore _assetStore = Substitute.For<IAssetStore>();
    private readonly GetSellerAssetDetailQueryHandler _handler;

    public GetSellerAssetDetailQueryHandlerTests()
    {
        _handler = new GetSellerAssetDetailQueryHandler(_assetStore);
    }

    [Fact]
    public async Task Handle_WhenMissingDeletedOrForeign_ShouldReturnNotFound()
    {
        var query = new GetSellerAssetDetailQuery(Guid.NewGuid(), Guid.NewGuid());
        _assetStore.GetOwnedSellerDetail(query.AssetId, query.OwnerUserId, Arg.Any<CancellationToken>())
            .Returns((SellerAssetDetailItem?)null);

        Result<SellerAssetDetailItem> result = await _handler.Handle(query, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Theory]
    [InlineData(AssetVersionProcessingStatus.PENDING_INSPECTION)]
    [InlineData(AssetVersionProcessingStatus.READY)]
    [InlineData(AssetVersionProcessingStatus.REJECTED)]
    [InlineData(AssetVersionProcessingStatus.PROCESSING_FAILED)]
    public async Task Handle_WhenOwnedAssetExists_ShouldReturnSellerDetail(AssetVersionProcessingStatus status)
    {
        var assetId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid? currentReady = status == AssetVersionProcessingStatus.READY ? versionId : null;
        var item = new SellerAssetDetailItem(
            assetId,
            "Pack",
            "  ",
            12m,
            Guid.NewGuid(),
            "3D",
            ownerId,
            "seller",
            now,
            null,
            ["lowpoly"],
            versionId,
            1,
            currentReady,
            status,
            now,
            status == AssetVersionProcessingStatus.REJECTED ? "MALWARE_DETECTED" : null,
            status == AssetVersionProcessingStatus.REJECTED ? "Malware was detected." : null);

        _assetStore.GetOwnedSellerDetail(assetId, ownerId, Arg.Any<CancellationToken>()).Returns(item);

        Result<SellerAssetDetailItem> result = await _handler.Handle(new GetSellerAssetDetailQuery(assetId, ownerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(assetId);
        result.Value.Description.Should().BeNull();
        result.Value.LatestProcessingStatus.Should().Be(status);
        result.Value.CurrentReadyVersionId.Should().Be(currentReady);
    }
}
