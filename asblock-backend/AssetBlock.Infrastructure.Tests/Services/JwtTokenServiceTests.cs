using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Services;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class JwtTokenServiceTests
{
    [Fact]
    public async Task StoreRefreshToken_ValidateRefreshToken_roundtrip()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Username = "tester",
            Email = "t@test.com",
            PasswordHash = "hash",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var tokens = sut.GenerateTokenPair(userId, "tester", "t@test.com", AppRoles.USER);
        await sut.StoreRefreshToken(userId, tokens.RefreshToken, DateTimeOffset.UtcNow.AddDays(1));

        var validated = await sut.ValidateRefreshToken(tokens.RefreshToken);
        validated.Should().NotBeNull();
        validated!.Value.UserId.Should().Be(userId);
        validated.Value.Username.Should().Be("tester");
    }

    [Fact]
    public async Task RevokeRefreshToken_makes_validation_fail()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Username = "tester",
            Email = "t@test.com",
            PasswordHash = "hash",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var tokens = sut.GenerateTokenPair(userId, "tester", "t@test.com", AppRoles.USER);
        await sut.StoreRefreshToken(userId, tokens.RefreshToken, DateTimeOffset.UtcNow.AddDays(1));

        var validated = await sut.ValidateRefreshToken(tokens.RefreshToken);
        validated.Should().NotBeNull();
        await sut.RevokeRefreshToken(validated!.Value.TokenId);

        (await sut.ValidateRefreshToken(tokens.RefreshToken)).Should().BeNull();
    }

    [Fact]
    public async Task ValidateRefreshToken_returnsNull_whenExpired()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Username = "tester",
            Email = "t@test.com",
            PasswordHash = "hash",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var tokens = sut.GenerateTokenPair(userId, "tester", "t@test.com", AppRoles.USER);
        await sut.StoreRefreshToken(userId, tokens.RefreshToken, DateTimeOffset.UtcNow.AddDays(-1));

        (await sut.ValidateRefreshToken(tokens.RefreshToken)).Should().BeNull();
    }

    [Fact]
    public async Task RevokeRefreshToken_whenTokenMissing_isNoOp()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var sut = CreateSut(db);
        await sut.RevokeRefreshToken(Guid.NewGuid());
    }

    private static JwtTokenService CreateSut(ApplicationDbContext db)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new JwtOptions
        {
            Key = new string('k', 32),
            Issuer = "iss",
            Audience = "aud",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7
        });
        return new JwtTokenService(db, opts, NullLogger<JwtTokenService>.Instance);
    }
}
