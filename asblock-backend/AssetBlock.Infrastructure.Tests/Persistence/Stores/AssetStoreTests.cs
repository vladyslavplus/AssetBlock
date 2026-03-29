using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Tests.Infrastructure;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class AssetStoreTests
{
    [Fact]
    public async Task Add_GetById_GetPaged_Update_Delete_AddRemoveTag()
    {
        await using var holder = new SqliteDbContextHolder();
        var db = holder.Context;
        var catId = Guid.NewGuid();
        db.Categories.Add(new Category
        {
            Id = catId,
            Name = "C",
            Slug = "c",
            CreatedAt = DateTimeOffset.UtcNow
        });
        var authorId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = authorId,
            Username = "a",
            Email = "a@a.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new AssetStore(db);
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            AuthorId = authorId,
            CategoryId = catId,
            Title = "Title",
            Description = "Desc",
            Price = 10,
            StorageKey = "k",
            FileName = "f.zip",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await sut.Add(asset);

        var loaded = await sut.GetById(asset.Id);
        loaded!.Title.Should().Be("Title");
        loaded.Category.Should().NotBeNull();

        var paged = await sut.GetPaged(new GetAssetsRequest { Search = "title", SortBy = "Price", SortDirection = SortDirection.ASC });
        paged.Items.Should().Contain(a => a.Id == asset.Id);

        (await sut.Update(asset.Id, "New", null, 20m, null)).Should().BeTrue();

        var tag = new Tag { Id = Guid.NewGuid(), Name = "t1", CreatedAt = DateTimeOffset.UtcNow };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        await sut.AddTag(asset.Id, tag.Id);
        (await sut.HasAssetTag(asset.Id, tag.Id)).Should().BeTrue();
        (await sut.RemoveTag(asset.Id, tag.Id)).Should().BeTrue();

        await sut.Delete(asset.Id);
        (await sut.GetById(asset.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Update_returnsFalse_whenAssetMissing()
    {
        await using var holder = new SqliteDbContextHolder();
        var sut = new AssetStore(holder.Context);
        (await sut.Update(Guid.NewGuid(), "x", null, null, null)).Should().BeFalse();
    }

    [Fact]
    public async Task AddWithTags_linksTags()
    {
        await using var holder = new SqliteDbContextHolder();
        var db = holder.Context;
        var catId = Guid.NewGuid();
        db.Categories.Add(new Category { Id = catId, Name = "C", Slug = "c", CreatedAt = DateTimeOffset.UtcNow });
        var authorId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = authorId,
            Username = "a",
            Email = "a@a.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var tag = new Tag { Id = Guid.NewGuid(), Name = "linked", CreatedAt = DateTimeOffset.UtcNow };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        var sut = new AssetStore(db);
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            AuthorId = authorId,
            CategoryId = catId,
            Title = "A",
            StorageKey = "k",
            FileName = "f",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await sut.AddWithTags(asset, [tag]);

        var loaded = await sut.GetById(asset.Id);
        loaded!.AssetTags.Should().HaveCount(1);
    }
}
