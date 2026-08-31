using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Services;

[Collection(nameof(PostgresStoreCollection))]
public sealed class JwtTokenServicePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RevokeRefreshToken_makes_validation_fail()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
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

        JwtTokenService sut = CreateSut(db);
        TokensResponse tokens = sut.GenerateTokenPair(userId, "tester", "t@test.com", AppRoles.USER);
        await sut.StoreRefreshToken(userId, tokens.RefreshToken, DateTimeOffset.UtcNow.AddDays(1));

        RefreshTokenValidationResult validated = await sut.ValidateRefreshToken(tokens.RefreshToken);
        validated.Status.Should().Be(RefreshTokenValidationStatus.VALID);
        var revoked = await sut.RevokeRefreshToken(validated.TokenId!.Value);
        revoked.Should().BeTrue();

        RefreshTokenValidationResult afterRevocation = await sut.ValidateRefreshToken(tokens.RefreshToken);
        afterRevocation.Status.Should().NotBe(RefreshTokenValidationStatus.VALID);
    }

    [Fact]
    public async Task ValidateRefreshToken_WhenReusedBeyondGrace_ReturnsRevokedReused()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
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

        JwtTokenService sut = CreateSut(db);
        TokensResponse tokens = sut.GenerateTokenPair(userId, "tester_reuse", "reuse@test.com", AppRoles.USER);
        await sut.StoreRefreshToken(userId, tokens.RefreshToken, DateTimeOffset.UtcNow.AddDays(1));

        RefreshTokenValidationResult validated = await sut.ValidateRefreshToken(tokens.RefreshToken);
        validated.Status.Should().Be(RefreshTokenValidationStatus.VALID);
        await sut.RevokeRefreshToken(validated.TokenId!.Value);

        // Manually age the RevokedAt timestamp past the 15s grace window
        RefreshToken? storedToken = await db.RefreshTokens.FindAsync(validated.TokenId!.Value);
        storedToken!.RevokedAt = DateTimeOffset.UtcNow.AddSeconds(-60);
        await db.SaveChangesAsync();

        RefreshTokenValidationResult reuseCheck = await sut.ValidateRefreshToken(tokens.RefreshToken);
        reuseCheck.Status.Should().Be(RefreshTokenValidationStatus.REVOKED_REUSED);
        reuseCheck.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task RevokeRefreshToken_whenTokenMissing_isNoOp()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        JwtTokenService sut = CreateSut(db);
        var revoked = await sut.RevokeRefreshToken(Guid.NewGuid());
        revoked.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeRefreshToken_ConcurrentCalls_OnlyOneSucceeds()
    {
        await using ApplicationDbContext db1 = await fixture.CreateCleanDbContext();
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

        JwtTokenService sut1 = CreateSut(db1);
        TokensResponse tokens = sut1.GenerateTokenPair(userId, "tester_conc", "conc@test.com", AppRoles.USER);
        await sut1.StoreRefreshToken(userId, tokens.RefreshToken, DateTimeOffset.UtcNow.AddDays(1));
        RefreshTokenValidationResult validated = await sut1.ValidateRefreshToken(tokens.RefreshToken);
        validated.Status.Should().Be(RefreshTokenValidationStatus.VALID);

        await using ApplicationDbContext db2 = fixture.CreateDbContext();
        JwtTokenService sut2 = CreateSut(db2);

        using var barrier = new Barrier(2);
        Task<bool> task1 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await sut1.RevokeRefreshToken(validated.TokenId!.Value);
        });
        Task<bool> task2 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await sut2.RevokeRefreshToken(validated.TokenId!.Value);
        });

        var results = await Task.WhenAll(task1, task2);

        results.Count(r => r).Should().Be(1);
        results.Count(r => !r).Should().Be(1);
    }

    [Fact]
    public async Task CleanupExpiredTokens_ShouldDeleteExpiredAndPreserveUnexpiredRevoked()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Username = "retention_tester",
            Email = "retention@test.com",
            PasswordHash = "hash",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        JwtTokenService sut = CreateSut(db);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // 1. Expired token (should be deleted)
        TokensResponse t1 = sut.GenerateTokenPair(userId, "retention_tester", "retention@test.com", AppRoles.USER);
        await sut.StoreRefreshToken(userId, t1.RefreshToken, now.AddHours(-2));

        // 2. Another expired token (should be deleted)
        TokensResponse t2 = sut.GenerateTokenPair(userId, "retention_tester", "retention@test.com", AppRoles.USER);
        await sut.StoreRefreshToken(userId, t2.RefreshToken, now.AddMinutes(-5));

        // 3. Unexpired active token (should be preserved)
        TokensResponse t3 = sut.GenerateTokenPair(userId, "retention_tester", "retention@test.com", AppRoles.USER);
        await sut.StoreRefreshToken(userId, t3.RefreshToken, now.AddDays(2));

        // 4. Unexpired revoked token (should be preserved for grace window / reuse detection)
        TokensResponse t4 = sut.GenerateTokenPair(userId, "retention_tester", "retention@test.com", AppRoles.USER);
        await sut.StoreRefreshToken(userId, t4.RefreshToken, now.AddDays(1));
        RefreshTokenValidationResult val4 = await sut.ValidateRefreshToken(t4.RefreshToken);
        await sut.RevokeRefreshToken(val4.TokenId!.Value);

        var deleted = await sut.CleanupExpiredTokens(now, batchSize: 10);
        deleted.Should().Be(2);

        var remaining = db.RefreshTokens.Where(r => r.UserId == userId).ToList();
        remaining.Should().HaveCount(2);
        remaining.Should().Contain(r => r.ExpiresAt > now);
    }

    private static JwtTokenService CreateSut(ApplicationDbContext db)
    {
        IOptions<JwtOptions> opts = Microsoft.Extensions.Options.Options.Create(new JwtOptions
        {
            Key = new string('k', 32),
            Issuer = "iss",
            Audience = "aud",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
            HubAudience = "hub-aud",
            HubTokenSeconds = 90
        });
        return new JwtTokenService(db, opts, NullLogger<JwtTokenService>.Instance);
    }
}
