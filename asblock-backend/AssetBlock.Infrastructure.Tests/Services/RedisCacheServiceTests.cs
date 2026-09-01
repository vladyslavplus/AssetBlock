using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class RedisCacheServiceTests
{
    [Fact]
    public async Task GetString_returnsValue_whenRedisReturnsValue()
    {
        IConnectionMultiplexer mux = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        mux.GetDatabase().Returns(db);
        db.StringGetAsync(Arg.Any<RedisKey>())
            .Returns(Task.FromResult(new RedisValue("hello")));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        (await sut.GetString("k")).Should().Be("hello");
    }

    [Fact]
    public async Task GetString_returnsNull_whenKeyIsMissing()
    {
        IConnectionMultiplexer mux = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        mux.GetDatabase().Returns(db);
        db.StringGetAsync(Arg.Any<RedisKey>()).Returns(Task.FromResult(RedisValue.Null));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        (await sut.GetString("k")).Should().BeNull();
    }

    [Fact]
    public async Task GetString_whenRedisThrows_throwsUnavailableAndOpensCircuitAfterThreshold()
    {
        IConnectionMultiplexer mux = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        var time = new FakeTimeProvider();
        var unavailable = true;
        mux.GetDatabase().Returns(db);
        db.StringGetAsync(Arg.Any<RedisKey>())
            .Returns(_ => unavailable
                ? Task.FromException<RedisValue>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"))
                : Task.FromResult(new RedisValue("recovered")));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance, time);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            Func<Task<string?>> act = () => sut.GetString("k");
            await act.Should().ThrowAsync<CacheUnavailableException>();
        }

        await db.Received(3).StringGetAsync(Arg.Any<RedisKey>());

        time.Advance(TimeSpan.FromSeconds(31));
        unavailable = false;
        (await sut.GetString("k")).Should().Be("recovered");
        await db.Received(4).StringGetAsync(Arg.Any<RedisKey>());
    }

    [Fact]
    public async Task SetString_withExpiry_callsStringSet()
    {
        IConnectionMultiplexer mux = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        mux.GetDatabase().Returns(db);
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>())
            .Returns(Task.FromResult(true));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        await sut.SetString("k", "v", TimeSpan.FromMinutes(1));

        await db.Received(1).StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>());
    }

    [Fact]
    public async Task SetString_forIndexedKey_usesAtomicMembershipScript()
    {
        IConnectionMultiplexer mux = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        mux.GetDatabase().Returns(db);
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(RedisResult.Create(1));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        await sut.SetString(CacheKeys.ASSETS_LIST_PREFIX + ":page", "v", TimeSpan.FromMinutes(5));

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(keys =>
                keys.Length == 2
                && keys[0] == CacheKeys.ASSETS_LIST_PREFIX + ":page"
                && keys[1] == CacheKeys.InvalidationIndex(CacheKeys.ASSETS_LIST_PREFIX)),
            Arg.Any<RedisValue[]>());
    }

    [Fact]
    public async Task Increment_setsExpiryOnFirstHit()
    {
        IConnectionMultiplexer mux = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        mux.GetDatabase().Returns(db);
        db.StringIncrementAsync(Arg.Any<RedisKey>())
            .Returns(Task.FromResult(1L));
        db.KeyTimeToLiveAsync(Arg.Any<RedisKey>())
            .Returns(Task.FromResult<TimeSpan?>(null));
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult(true));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        var n = await sut.Increment("c", TimeSpan.FromMinutes(5));
        n.Should().Be(1);
        await db.Received(1).KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task RemoveByPrefix_takesIndexedMembersAndDeletesOnlyThoseKeys()
    {
        IConnectionMultiplexer mux = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        mux.GetDatabase().Returns(db);
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(RedisResult.Create(["pre:a", "pre:b"]));
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(2L));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        await sut.RemoveByPrefix("pre");

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(keys => keys.SequenceEqual(new RedisKey[] { CacheKeys.InvalidationIndex("pre") })),
            Arg.Any<RedisValue[]>());
        await db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(keys => keys.SequenceEqual(new RedisKey[] { "pre:a", "pre:b" })),
            Arg.Any<CommandFlags>());
        mux.DidNotReceiveWithAnyArgs().GetEndPoints(default);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
