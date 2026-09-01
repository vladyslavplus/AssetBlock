using AssetBlock.Infrastructure.Services;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class MemoryCacheServiceTests
{
    private readonly MemoryCacheService _sut = new();
    private static readonly TimeSpan _ttl = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task SetString_GetString_roundtrip()
    {
        await _sut.SetString("k1", "v1", _ttl);
        (await _sut.GetString("k1")).Should().Be("v1");
    }

    [Fact]
    public async Task SetString_nonPositiveExpiration_throws()
    {
        Func<Task> act = () => _sut.SetString("k0", "v0", TimeSpan.Zero);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Remove_deletesExactKey()
    {
        await _sut.SetString("k1", "v1", _ttl);
        await _sut.Remove("k1");
        (await _sut.GetString("k1")).Should().BeNull();
    }

    [Fact]
    public async Task GetString_RemovesExpired()
    {
        var time = new FakeTimeProvider();
        var sut = new MemoryCacheService(time);
        await sut.SetString("k2", "v2", TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        (await sut.GetString("k2")).Should().BeNull();
    }

    [Fact]
    public async Task RemoveByPrefix_removesMatchingKeys()
    {
        await _sut.SetString("p:a", "1", _ttl);
        await _sut.SetString("p:b", "2", _ttl);
        await _sut.SetString("q:a", "3", _ttl);
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

    [Fact]
    public async Task SetString_whenCapacityReached_evictsOldestEntry()
    {
        var sut = new MemoryCacheService(maxEntries: 2);
        await sut.SetString("first", "1", _ttl);
        await sut.SetString("second", "2", _ttl);
        await sut.SetString("third", "3", _ttl);

        (await sut.GetString("first")).Should().BeNull();
        (await sut.GetString("second")).Should().Be("2");
        (await sut.GetString("third")).Should().Be("3");
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
