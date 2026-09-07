using AssetBlock.Infrastructure.Services;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class BoundedQueryVectorCacheTests
{
    private const string MODEL_KEY = "e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6";
    private readonly TestTimeProvider _timeProvider = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private sealed class TestTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    [Fact]
    public void Get_WhenKeyNotFound_ReturnsNull()
    {
        var sut = new BoundedQueryVectorCache(_timeProvider, maxEntries: 10);
        var result = sut.Get(MODEL_KEY, "nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public void SetAndGet_ReturnsVector()
    {
        var sut = new BoundedQueryVectorCache(_timeProvider, maxEntries: 10);
        var vector = new[] { 1.0f, 2.0f, 3.0f };

        sut.Set(MODEL_KEY, "hash1", vector, TimeSpan.FromMinutes(15));
        var result = sut.Get(MODEL_KEY, "hash1");

        result.Should().NotBeNull();
        result.Should().Equal(vector);
    }

    [Fact]
    public void Get_WhenExpired_ReturnsNullAndEvicts()
    {
        var sut = new BoundedQueryVectorCache(_timeProvider, maxEntries: 10);
        var vector = new[] { 1.0f, 2.0f };

        sut.Set(MODEL_KEY, "hash1", vector, TimeSpan.FromMinutes(15));
        _timeProvider.Advance(TimeSpan.FromMinutes(16));

        var result = sut.Get(MODEL_KEY, "hash1");

        result.Should().BeNull();
        sut.Count.Should().Be(0);
    }

    [Fact]
    public void Set_WhenCapacityExceeded_EvictsLeastRecentlyUsed()
    {
        var sut = new BoundedQueryVectorCache(_timeProvider, maxEntries: 3);

        sut.Set(MODEL_KEY, "k1", [1.0f], TimeSpan.FromMinutes(15));
        sut.Set(MODEL_KEY, "k2", [2.0f], TimeSpan.FromMinutes(15));
        sut.Set(MODEL_KEY, "k3", [3.0f], TimeSpan.FromMinutes(15));

        sut.Count.Should().Be(3);

        // Access k1, making k2 the LRU (k1 has highest sequence, k2 has lowest)
        sut.Get(MODEL_KEY, "k1");

        // Add k4 -> should evict k2
        sut.Set(MODEL_KEY, "k4", [4.0f], TimeSpan.FromMinutes(15));

        sut.Count.Should().Be(3);
        sut.Get(MODEL_KEY, "k2").Should().BeNull();
        sut.Get(MODEL_KEY, "k1").Should().NotBeNull();
        sut.Get(MODEL_KEY, "k3").Should().NotBeNull();
        sut.Get(MODEL_KEY, "k4").Should().NotBeNull();
    }

    [Fact]
    public void Set_WhenExpiredPresent_EvictsExpiredFirstInsteadOfActiveLRU()
    {
        var sut = new BoundedQueryVectorCache(_timeProvider, maxEntries: 2);

        sut.Set(MODEL_KEY, "short", [1.0f], TimeSpan.FromMinutes(5));
        sut.Set(MODEL_KEY, "long", [2.0f], TimeSpan.FromMinutes(30));

        // Advance 10 mins -> "short" is expired, "long" is active
        _timeProvider.Advance(TimeSpan.FromMinutes(10));

        // Add new entry -> capacity is full, should evict expired "short" and keep "long"
        sut.Set(MODEL_KEY, "new", [3.0f], TimeSpan.FromMinutes(15));

        sut.Count.Should().Be(2);
        sut.Get(MODEL_KEY, "short").Should().BeNull();
        sut.Get(MODEL_KEY, "long").Should().NotBeNull();
        sut.Get(MODEL_KEY, "new").Should().NotBeNull();
    }

    [Fact]
    public void DefensiveCopy_MutatingReturnedArrayDoesNotMutateCache()
    {
        var sut = new BoundedQueryVectorCache(_timeProvider, maxEntries: 5);
        var original = new[] { 10.0f, 20.0f };

        sut.Set(MODEL_KEY, "k", original, TimeSpan.FromMinutes(15));

        var copy = sut.Get(MODEL_KEY, "k");
        copy![0] = 999.0f;

        var fetchedAgain = sut.Get(MODEL_KEY, "k");
        fetchedAgain![0].Should().Be(10.0f);
    }

    [Fact]
    public void DefensiveCopy_MutatingInputArrayAfterSetDoesNotMutateCache()
    {
        var sut = new BoundedQueryVectorCache(_timeProvider, maxEntries: 5);
        var original = new[] { 10.0f, 20.0f };

        sut.Set(MODEL_KEY, "k", original, TimeSpan.FromMinutes(15));
        original[0] = 999.0f;

        var fetched = sut.Get(MODEL_KEY, "k");
        fetched![0].Should().Be(10.0f);
    }
}
