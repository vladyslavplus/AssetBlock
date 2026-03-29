using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class CategoryStoreTests
{
    [Fact]
    public async Task Create_GetById_GetPaged_SlugExists_Update_Delete()
    {
        await using var holder = new SqliteDbContextHolder();
        var db = holder.Context;
        var sut = new CategoryStore(db, NullLogger<CategoryStore>.Instance);

        var created = await sut.Create("Algorithms", "desc", "algorithms");
        created.Id.Should().NotBeEmpty();

        (await sut.GetById(created.Id))!.Slug.Should().Be("algorithms");

        (await sut.SlugExists("algorithms", null)).Should().BeTrue();
        (await sut.SlugExists("algorithms", created.Id)).Should().BeFalse();

        var paged = await sut.GetPaged(new GetCategoriesRequest { Page = 1, PageSize = 10 });
        paged.Items.Should().Contain(c => c.Id == created.Id);

        created.Name = "Algorithms2";
        await sut.Update(created);

        (await sut.GetById(created.Id))!.Name.Should().Be("Algorithms2");

        (await sut.Delete(created.Id)).Should().BeTrue();
        (await sut.GetById(created.Id)).Should().BeNull();
    }

    [Fact]
    public async Task GetPaged_searchAndSort()
    {
        await using var holder = new SqliteDbContextHolder();
        var db = holder.Context;
        var sut = new CategoryStore(db, NullLogger<CategoryStore>.Instance);
        await sut.Create("Alpha", null, "alpha");
        await sut.Create("Beta", null, "beta");

        var bySearch = await sut.GetPaged(new GetCategoriesRequest { Search = "beta" });
        bySearch.Items.Should().HaveCount(1);
        bySearch.Items[0].Slug.Should().Be("beta");

        var bySlug = await sut.GetPaged(new GetCategoriesRequest { SortBy = "Slug", SortDirection = SortDirection.DESC });
        bySlug.Items.Select(c => c.Slug).Should().BeInDescendingOrder();
    }
}
