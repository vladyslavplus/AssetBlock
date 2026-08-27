using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
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
        validated.Status.Should().Be(RefreshTokenValidationStatus.VALID);
        var revoked = await sut.RevokeRefreshToken(validated.TokenId!.Value);
        revoked.Should().BeTrue();

        var afterRevocation = await sut.ValidateRefreshToken(tokens.RefreshToken);
        afterRevocation.Status.Should().NotBe(RefreshTokenValidationStatus.VALID);
    }

    [Fact]
    public async Task ValidateRefreshToken_WhenReusedBeyondGrace_ReturnsRevokedReused()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Username = "tester_reuse",
            Email = "reuse@test.com",
            PasswordHash = "hash",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var tokens = sut.GenerateTokenPair(userId, "tester_reuse", "reuse@test.com", AppRoles.USER);
        await sut.StoreRefreshToken(userId, tokens.RefreshToken, DateTimeOffset.UtcNow.AddDays(1));

        var validated = await sut.ValidateRefreshToken(tokens.RefreshToken);
        validated.Status.Should().Be(RefreshTokenValidationStatus.VALID);
        await sut.RevokeRefreshToken(validated.TokenId!.Value);

        // Manually age the RevokedAt timestamp past the 15s grace window
        var storedToken = await db.RefreshTokens.FindAsync(validated.TokenId!.Value);
        storedToken!.RevokedAt = DateTimeOffset.UtcNow.AddSeconds(-60);
        await db.SaveChangesAsync();

        var reuseCheck = await sut.ValidateRefreshToken(tokens.RefreshToken);
        reuseCheck.Status.Should().Be(RefreshTokenValidationStatus.REVOKED_REUSED);
        reuseCheck.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task RevokeRefreshToken_whenTokenMissing_isNoOp()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var sut = CreateSut(db);
        var revoked = await sut.RevokeRefreshToken(Guid.NewGuid());
        revoked.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeRefreshToken_ConcurrentCalls_OnlyOneSucceeds()
    {
        await using var db1 = await fixture.CreateCleanDbContext();
        var userId = Guid.NewGuid();
        db1.Users.Add(new User
        {
            Id = userId,
            Username = "tester_conc",
            Email = "conc@test.com",
            PasswordHash = "hash",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db1.SaveChangesAsync();

        var sut1 = CreateSut(db1);
        var tokens = sut1.GenerateTokenPair(userId, "tester_conc", "conc@test.com", AppRoles.USER);
        await sut1.StoreRefreshToken(userId, tokens.RefreshToken, DateTimeOffset.UtcNow.AddDays(1));
        var validated = await sut1.ValidateRefreshToken(tokens.RefreshToken);
        validated.Status.Should().Be(RefreshTokenValidationStatus.VALID);

        await using var db2 = fixture.CreateDbContext();
        var sut2 = CreateSut(db2);

        using var barrier = new Barrier(2);
        var task1 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await sut1.RevokeRefreshToken(validated.TokenId!.Value);
        });
        var task2 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await sut2.RevokeRefreshToken(validated.TokenId!.Value);
        });

        var results = await Task.WhenAll(task1, task2);

        results.Count(r => r).Should().Be(1);
        results.Count(r => !r).Should().Be(1);
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
