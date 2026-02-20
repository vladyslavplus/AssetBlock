using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class GetAssetsQueryHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly GetAssetsQueryHandler _handler;

    public GetAssetsQueryHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _cacheMock = Substitute.For<ICacheService>();
        _handler = new GetAssetsQueryHandler(
            _assetStoreMock,
            _cacheMock,
            NullLogger<GetAssetsQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCacheHit_ShouldReturnCachedResultWithoutCallingStore()
    {
        // Arrange
        const string cachedJson = """{"items":[{"id":"00000000-0000-0000-0000-000000000001","title":"Cached Asset","description":null,"price":9.99,"categoryId":"00000000-0000-0000-0000-000000000002","categoryName":"Audio","authorId":"00000000-0000-0000-0000-000000000003","createdAt":"2024-01-01T00:00:00+00:00"}],"totalCount":1,"page":1,"pageSize":10,"totalPages":1}""";
        _cacheMock.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(cachedJson);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10 };
        var query = new GetAssetsQuery(request);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Cached Asset");

        // Store should NOT be called
        await _assetStoreMock.DidNotReceiveWithAnyArgs().GetPaged(null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenCacheMiss_ShouldFetchFromStoreAndCacheResult()
    {
        // Arrange
        _cacheMock.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var categoryId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var category = new Category { Id = categoryId, Name = "3D Models", Slug = "3d-models" };
        var storedAssets = new List<Asset>
        {
            new() {
                Id = Guid.NewGuid(), AuthorId = authorId, CategoryId = categoryId,
                Title = "Low-Poly Tree", Price = 4.99m, StorageKey = "s/k", FileName = "tree.fbx",
                CreatedAt = DateTimeOffset.UtcNow, Category = category
            }
        };

        var pagedResult = new PagedResult<Asset>(storedAssets, 1, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), Arg.Any<CancellationToken>()).Returns(pagedResult);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10 };
        var query = new GetAssetsQuery(request);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Low-Poly Tree");
        result.Value.TotalCount.Should().Be(1);

        // Should have cached the result
        await _cacheMock.Received(1)
            .SetString(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCacheContainsCorruptJson_ShouldInvalidateCacheAndFetchFromStore()
    {
        // Arrange
        _cacheMock.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("this is NOT valid JSON {{{{");

        var emptyPaged = new PagedResult<Asset>([], 0, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), Arg.Any<CancellationToken>()).Returns(emptyPaged);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10 };
        var query = new GetAssetsQuery(request);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert: a corrupt cache should trigger a fallback to store
        result.IsSuccess.Should().BeTrue();
        await _assetStoreMock.Received(1).GetPaged(Arg.Any<GetAssetsRequest>(), Arg.Any<CancellationToken>());
        // Cache should have been cleared first
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenStorageIsEmpty_ShouldReturnEmptyPagedResult()
    {
        // Arrange
        _cacheMock.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var emptyPaged = new PagedResult<Asset>([], 0, 1, 10);
        _assetStoreMock.GetPaged(Arg.Any<GetAssetsRequest>(), Arg.Any<CancellationToken>()).Returns(emptyPaged);

        var request = new GetAssetsRequest { Page = 1, PageSize = 10 };
        var query = new GetAssetsQuery(request);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }
}
