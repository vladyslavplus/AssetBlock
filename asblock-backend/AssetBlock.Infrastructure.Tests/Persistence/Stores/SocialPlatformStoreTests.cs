using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using AssetBlock.Infrastructure.Tests.Infrastructure;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class SocialPlatformStoreTests
{
    [Fact]
    public async Task GetAll_cachesPlatforms()
    {
        await using ApplicationDbContext db = InMemoryDbContextFactory.Create();
        var cache = new MemoryCacheService();
        var sut = new SocialPlatformStore(db, cache);

        List<SocialPlatform> all = await sut.GetAll();
        all.Should().NotBeEmpty();

        var cached = await cache.GetString(CacheKeys.SOCIAL_PLATFORMS);
        cached.Should().NotBeNullOrEmpty();

        List<SocialPlatform> second = await sut.GetAll();
        second.Should().HaveCount(all.Count);
        second[0].Id.Should().Be(all[0].Id);
    }

    [Fact]
    public async Task GetAll_whenCachedJsonIsNullLiteral_loadsFromDatabase()
    {
        await using ApplicationDbContext db = InMemoryDbContextFactory.Create();
        var cache = new MemoryCacheService();
        await cache.SetString(CacheKeys.SOCIAL_PLATFORMS, "null");

        var sut = new SocialPlatformStore(db, cache);
        List<SocialPlatform> all = await sut.GetAll();
        all.Should().NotBeEmpty();
    }
}
