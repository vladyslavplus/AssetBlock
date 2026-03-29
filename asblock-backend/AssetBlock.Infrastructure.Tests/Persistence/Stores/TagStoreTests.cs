using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Tests.Infrastructure;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class TagStoreTests
{
    [Fact]
    public async Task Add_GetById_GetByName_GetTagsByNames_SearchTags_GetPaged()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var sut = new TagStore(db);

        var tag = new Tag { Id = Guid.NewGuid(), Name = "unity", CreatedAt = DateTimeOffset.UtcNow };
        await sut.Add(tag);

        (await sut.GetById(tag.Id))!.Name.Should().Be("unity");
        (await sut.GetByName("UNITY"))!.Id.Should().Be(tag.Id);

        var byNames = await sut.GetTagsByNames(["unity", "missing"]);
        byNames.Should().HaveCount(1);

        var paged = await sut.SearchTags(new GetTagsRequest { Page = 1, PageSize = 10 });
        paged.TotalCount.Should().BeGreaterThanOrEqualTo(1);

        var pagedById = await sut.SearchTags(new GetTagsRequest { SortBy = "id", SortDirection = SortDirection.DESC });
        pagedById.Items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Update_Delete()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var sut = new TagStore(db);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "tmp", CreatedAt = DateTimeOffset.UtcNow };
        await sut.Add(tag);
        tag.Name = "renamed";
        await sut.Update(tag);
        (await sut.GetById(tag.Id))!.Name.Should().Be("renamed");

        await sut.Delete(tag);
        (await sut.GetById(tag.Id)).Should().BeNull();
    }
}
