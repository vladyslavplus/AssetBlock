using AssetBlock.Infrastructure.Services;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class MemoryCacheServiceTests
{
    private readonly MemoryCacheService _sut = new();

    [Fact]
    public async Task SetString_GetString_roundtrip()
    {
        await _sut.SetString("k1", "v1");
        (await _sut.GetString("k1")).Should().Be("v1");
    }

    [Fact]
    public async Task SetString_withoutExpiration_stillReadable()
    {
        await _sut.SetString("k0", "v0", null);
        (await _sut.GetString("k0")).Should().Be("v0");
    }

    [Fact]
    public async Task GetString_RemovesExpired()
    {
        await _sut.SetString("k2", "v2", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        (await _sut.GetString("k2")).Should().BeNull();
    }

    [Fact]
    public async Task RemoveByPrefix_removesMatchingKeys()
    {
        await _sut.SetString("p:a", "1");
        await _sut.SetString("p:b", "2");
        await _sut.SetString("q:a", "3");
        await _sut.RemoveByPrefix("p:");
        (await _sut.GetString("p:a")).Should().BeNull();
        (await _sut.GetString("p:b")).Should().BeNull();
        (await _sut.GetString("q:a")).Should().Be("3");
    }

    [Fact]
    public async Task Increment_incrementsWithinWindow()
    {
        var key = "incr:" + Guid.NewGuid();
        var count1 = await _sut.Increment(key, TimeSpan.FromMinutes(5));
        var count2 = await _sut.Increment(key, TimeSpan.FromMinutes(5));
        count1.Should().Be(1);
        count2.Should().Be(2);
    }
}
