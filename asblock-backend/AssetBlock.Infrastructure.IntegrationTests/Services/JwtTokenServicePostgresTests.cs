using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.IntegrationTests.Services;

[Collection(nameof(PostgresStoreCollection))]
public sealed class JwtTokenServicePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RevokeRefreshToken_makes_validation_fail()
    {
        await using var db = await fixture.CreateCleanDbContext();
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
    public async Task RevokeRefreshToken_whenTokenMissing_isNoOp()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var sut = CreateSut(db);
        await sut.RevokeRefreshToken(Guid.NewGuid());
    }

    private static JwtTokenService CreateSut(AssetBlock.Infrastructure.Persistence.ApplicationDbContext db)
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
