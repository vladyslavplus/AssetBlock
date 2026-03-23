using AssetBlock.Application.UseCases.Categories.GetCategories;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Categories;

public class GetCategoriesQueryHandlerTests
{
    private readonly ICategoryStore _categoryStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly GetCategoriesQueryHandler _handler;

    public GetCategoriesQueryHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _cacheMock = Substitute.For<ICacheService>();
        _handler = new GetCategoriesQueryHandler(
            _categoryStoreMock,
            _cacheMock,
            NullLogger<GetCategoriesQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCacheHit_ShouldReturnCachedResultWithoutCallingStore()
    {
        // Arrange
        const string cachedJson = """{"items":[{"id":"00000000-0000-0000-0000-000000000001","name":"Cached Category","slug":"cached-ctg","description":"test"}],"totalCount":1,"page":1,"pageSize":10,"totalPages":1}""";
        _cacheMock.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(cachedJson);

        var request = new GetCategoriesRequest { Page = 1, PageSize = 10 };
        var query = new GetCategoriesQuery(request);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Name.Should().Be("Cached Category");

        await _categoryStoreMock.DidNotReceiveWithAnyArgs().GetPaged(null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenCacheMiss_ShouldFetchFromStoreAndCacheResult()
    {
        // Arrange
        _cacheMock.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var categoryId = Guid.NewGuid();
        var storedCategories = new List<Category>
        {
            new() { Id = categoryId, Name = "Audio", Slug = "audio", Description = "Desc" }
        };

        var pagedResult = new PagedResult<Category>(storedCategories, 1, 1, 10);
        _categoryStoreMock.GetPaged(Arg.Any<GetCategoriesRequest>(), Arg.Any<CancellationToken>()).Returns(pagedResult);

        var request = new GetCategoriesRequest { Page = 1, PageSize = 10 };
        var query = new GetCategoriesQuery(request);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Name.Should().Be("Audio");

        await _cacheMock.Received(1).SetString(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCacheCorrupt_ShouldInvalidateAndFetchFromStore()
    {
        // Arrange
        _cacheMock.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("corrupt json {{");

        var emptyPaged = new PagedResult<Category>([], 0, 1, 10);
        _categoryStoreMock.GetPaged(Arg.Any<GetCategoriesRequest>(), Arg.Any<CancellationToken>()).Returns(emptyPaged);

        var request = new GetCategoriesRequest { Page = 1, PageSize = 10 };
        var query = new GetCategoriesQuery(request);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _categoryStoreMock.Received(1).GetPaged(Arg.Any<GetCategoriesRequest>(), Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCacheContainsNullJson_ShouldFetchFromStore()
    {
        _cacheMock.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("null");
        var emptyPaged = new PagedResult<Category>([], 0, 1, 10);
        _categoryStoreMock.GetPaged(Arg.Any<GetCategoriesRequest>(), Arg.Any<CancellationToken>()).Returns(emptyPaged);

        var query = new GetCategoriesQuery(new GetCategoriesRequest { Page = 1, PageSize = 10 });
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _categoryStoreMock.Received(1).GetPaged(Arg.Any<GetCategoriesRequest>(), Arg.Any<CancellationToken>());
    }
}
