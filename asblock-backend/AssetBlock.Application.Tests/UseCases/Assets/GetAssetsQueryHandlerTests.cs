using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class GetAssetsQueryHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly ITypedCache _cacheMock;
    private readonly GetAssetsQueryHandler _handler;

    public GetAssetsQueryHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _cacheMock = Substitute.For<ITypedCache>();
        _handler = new GetAssetsQueryHandler(
            _assetStoreMock,
            _cacheMock,
            NullLogger<GetAssetsQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCacheHit_ShouldReturnCachedResultWithoutCallingStore()
    {
        var cachedItems = new List<AssetListItem>
        {
            new(
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                "Cached Asset",
                null,
                9.99m,
                Guid.Parse("00000000-0000-0000-0000-000000000002"),
                "Audio",
                Guid.Parse("00000000-0000-0000-0000-000000000003"),
                "seller",
                DateTimeOffset.Parse("2024-01-01T00:00:00+00:00"),
                [],
                0)
        };
        var cachedResult = new PagedResult<AssetListItem>(cachedItems, 1, 1, 10);
        _cacheMock.Get<PagedResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cachedResult);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10 };
        var query = new GetAssetsQuery(request);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Cached Asset");

        await _assetStoreMock.DidNotReceiveWithAnyArgs().GetPaged(null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenCacheMiss_ShouldFetchFromStoreAndCacheResult()
    {
        _cacheMock.Get<PagedResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PagedResult<AssetListItem>?)null);

        var categoryId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var storedAssets = new List<AssetListItem>
        {
            new(
                Guid.NewGuid(),
                "Low-Poly Tree",
                null,
                4.99m,
                categoryId,
                "3D Models",
                authorId,
                "testuser",
                DateTimeOffset.UtcNow,
                [],
                0)
        };

        var pagedResult = new PagedResult<AssetListItem>(storedAssets, 1, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), Arg.Any<CancellationToken>()).Returns(pagedResult);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10 };
        var query = new GetAssetsQuery(request);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Low-Poly Tree");
        result.Value.TotalCount.Should().Be(1);

        await _cacheMock.Received(1)
            .Set(Arg.Any<string>(), Arg.Any<PagedResult<AssetListItem>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenStorageIsEmpty_ShouldReturnEmptyPagedResult()
    {
        _cacheMock.Get<PagedResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PagedResult<AssetListItem>?)null);
        var emptyPaged = new PagedResult<AssetListItem>([], 0, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), Arg.Any<CancellationToken>()).Returns(emptyPaged);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10 };
        var query = new GetAssetsQuery(request);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_ShouldNormalizeTagListAndWhitespaceDescriptions()
    {
        _cacheMock.Get<PagedResult<AssetListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PagedResult<AssetListItem>?)null);
        var categoryId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var items = new List<AssetListItem>
        {
            new(
                Guid.NewGuid(),
                "A",
                "   ",
                1,
                categoryId,
                "Cat",
                authorId,
                "u",
                DateTimeOffset.UtcNow,
                ["x"],
                0)
        };
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<AssetListItem>(items, 1, 1, 10));

        var query = new GetAssetsQuery(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Tags = ["alpha, Beta ", "alpha"]
        });

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].Description.Should().BeNull();
        await _assetStoreMock.Received(1).GetPaged(
            Arg.Is<GetAssetsRequest>(r =>
                r.Tags != null
                && r.Tags.Count == 2
                && r.Tags.Contains("alpha")
                && r.Tags.Contains("beta")),
            Arg.Any<CancellationToken>());
    }
}
