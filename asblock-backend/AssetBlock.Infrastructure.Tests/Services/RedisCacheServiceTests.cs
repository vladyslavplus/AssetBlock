using System.Net;
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
        var mux = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
        mux.GetDatabase().Returns(db);
        db.StringGetAsync(Arg.Any<RedisKey>())
            .Returns(Task.FromResult(new RedisValue("hello")));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        (await sut.GetString("k")).Should().Be("hello");
    }

    [Fact]
    public async Task GetString_returnsNull_whenRedisThrows()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
        mux.GetDatabase().Returns(db);
        db.StringGetAsync(Arg.Any<RedisKey>())
            .Returns(_ => Task.FromException<RedisValue>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down")));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        (await sut.GetString("k")).Should().BeNull();
    }

    [Fact]
    public async Task SetString_withExpiry_callsStringSet()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
        mux.GetDatabase().Returns(db);
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>())
            .Returns(Task.FromResult(true));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        await sut.SetString("k", "v", TimeSpan.FromMinutes(1));

        await db.Received(1).StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>());
    }

    [Fact]
    public async Task SetString_withoutExpiration_callsTwoArgOverload()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
        mux.GetDatabase().Returns(db);
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>())
            .Returns(Task.FromResult(true));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        await sut.SetString("k", "v");

        await db.Received(1).StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>());
    }

    [Fact]
    public async Task Increment_setsExpiryOnFirstHit()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
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
    public async Task RemoveByPrefix_iteratesKeysAndDeletes()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
        var server = Substitute.For<IServer>();
        mux.GetDatabase().Returns(db);
        mux.GetEndPoints().Returns([new DnsEndPoint("127.0.0.1", 6379)]);
        mux.GetServer(Arg.Any<EndPoint>()).Returns(server);
        server.Keys(Arg.Any<int>(), Arg.Any<RedisValue>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CommandFlags>())
            .Returns([new RedisKey("pre:a"), new RedisKey("pre:b")]);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(2L));

        var sut = new RedisCacheService(mux, NullLogger<RedisCacheService>.Instance);
        await sut.RemoveByPrefix("pre");

        await db.Received().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }
}
