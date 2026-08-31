using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.Categories.GetCategories;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Categories;

public class GetCategoriesQueryHandlerTests
{
    private readonly ICategoryStore _categoryStoreMock;
    private readonly ITypedCache _cacheMock;
    private readonly GetCategoriesQueryHandler _handler;

    public GetCategoriesQueryHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _cacheMock = Substitute.For<ITypedCache>();
        _handler = new GetCategoriesQueryHandler(
            _categoryStoreMock,
            _cacheMock,
            NullLogger<GetCategoriesQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCacheHit_ShouldReturnCachedResultWithoutCallingStore()
    {
        var cachedItems = new List<CategoryListItem>
        {
            new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Cached Category", "cached-ctg", "test")
        };
        var cachedResult = new PagedResult<CategoryListItem>(cachedItems, 1, 1, 10);
        _cacheMock.Get<PagedResult<CategoryListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cachedResult);

        var request = new GetCategoriesRequest { Page = 1, PageSize = 10 };
        var query = new GetCategoriesQuery(request);

        Ardalis.Result.Result<PagedResult<CategoryListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Name.Should().Be("Cached Category");

        await _categoryStoreMock.DidNotReceiveWithAnyArgs().GetPaged(null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenCacheMiss_ShouldFetchFromStoreAndCacheResult()
    {
        _cacheMock.Get<PagedResult<CategoryListItem>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PagedResult<CategoryListItem>?)null);

        var categoryId = Guid.NewGuid();
        var storedCategories = new List<Category>
        {
            new() { Id = categoryId, Name = "Audio", Slug = "audio", Description = "Desc" }
        };

        var pagedResult = new PagedResult<Category>(storedCategories, 1, 1, 10);
        _categoryStoreMock.GetPaged(Arg.Any<GetCategoriesRequest>(), Arg.Any<CancellationToken>()).Returns(pagedResult);

        var request = new GetCategoriesRequest { Page = 1, PageSize = 10 };
        var query = new GetCategoriesQuery(request);

        Ardalis.Result.Result<PagedResult<CategoryListItem>> result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Name.Should().Be("Audio");

        await _cacheMock.Received(1).Set(Arg.Any<string>(), Arg.Any<PagedResult<CategoryListItem>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
