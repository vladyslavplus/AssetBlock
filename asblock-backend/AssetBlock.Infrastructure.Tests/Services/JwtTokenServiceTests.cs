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
        validated.Status.Should().Be(AssetBlock.Domain.Core.Dto.Auth.RefreshTokenValidationStatus.VALID);
        validated.UserId.Should().Be(userId);
        validated.Username.Should().Be("tester");
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

        (await sut.ValidateRefreshToken(tokens.RefreshToken)).Status.Should().Be(AssetBlock.Domain.Core.Dto.Auth.RefreshTokenValidationStatus.NOT_FOUND_OR_EXPIRED);
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
