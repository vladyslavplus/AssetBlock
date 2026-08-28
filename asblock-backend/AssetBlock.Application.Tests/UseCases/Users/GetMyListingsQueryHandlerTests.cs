using AssetBlock.Application.UseCases.Users.GetMyListings;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public class GetMyListingsQueryHandlerTests
{
    private readonly IAssetStore _assetStoreMock = Substitute.For<IAssetStore>();
    private readonly GetMyListingsQueryHandler _handler;

    public GetMyListingsQueryHandlerTests()
    {
        _handler = new GetMyListingsQueryHandler(_assetStoreMock);
    }

    [Fact]
    public async Task Handle_WhenValidRequest_ShouldReturnNormalizedPagedListings()
    {
        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var rawItems = new List<SellerAssetListItem>
        {
            new(
                Guid.NewGuid(),
                "Listing 1",
                "   ",
                10m,
                categoryId,
                "Category",
                authorId,
                "Author",
                DateTimeOffset.UtcNow,
                ["3d"],
                0.0,
                Guid.NewGuid(),
                1,
                Guid.NewGuid(),
                AssetVersionProcessingStatus.READY,
                DateTimeOffset.UtcNow,
                null,
                null)
        };

        var pagedResult = new PagedResult<SellerAssetListItem>(rawItems, 1, 1, 10);
        _assetStoreMock.GetMyListings(authorId, Arg.Any<GetAssetsRequest>(), Arg.Any<CancellationToken>())
            .Returns(pagedResult);

        var query = new GetMyListingsQuery(authorId, new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Tags = ["tag1, TAG2 ", "tag1"]
        });

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Description.Should().BeNull();

        await _assetStoreMock.Received(1).GetMyListings(
            authorId,
            Arg.Is<GetAssetsRequest>(r =>
                r.Tags != null
                && r.Tags.Count == 2
                && r.Tags.Contains("tag1")
                && r.Tags.Contains("tag2")),
            Arg.Any<CancellationToken>());
    }
}
