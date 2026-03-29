using AssetBlock.Domain.Core.Constants;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using AssetBlock.Infrastructure.Tests.Infrastructure;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class SocialPlatformStoreTests
{
    [Fact]
    public async Task GetAll_cachesAndGetById()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var cache = new MemoryCacheService();
        var sut = new SocialPlatformStore(db, cache);

        var all = await sut.GetAll();
        all.Should().NotBeEmpty();

        var cached = await cache.GetString(CacheKeys.SOCIAL_PLATFORMS);
        cached.Should().NotBeNullOrEmpty();

        var second = await sut.GetAll();
        second.Should().HaveCount(all.Count);

        var id = all[0].Id;
        (await sut.GetById(id))!.Id.Should().Be(id);
    }

    [Fact]
    public async Task GetAll_whenCachedJsonIsNullLiteral_loadsFromDatabase()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var cache = new MemoryCacheService();
        await cache.SetString(CacheKeys.SOCIAL_PLATFORMS, "null");

        var sut = new SocialPlatformStore(db, cache);
        var all = await sut.GetAll();
        all.Should().NotBeEmpty();
    }
}
