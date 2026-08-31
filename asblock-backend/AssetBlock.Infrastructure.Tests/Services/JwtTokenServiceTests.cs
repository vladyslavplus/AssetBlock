using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Services;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class JwtTokenServiceTests
{
    [Fact]
    public async Task StoreRefreshToken_ValidateRefreshToken_roundtrip()
    {
        await using ApplicationDbContext db = InMemoryDbContextFactory.Create();
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
        validated.Status.Should().Be(Domain.Core.Dto.Auth.RefreshTokenValidationStatus.VALID);
        validated.UserId.Should().Be(userId);
        validated.Username.Should().Be("tester");
    }

    [Fact]
    public async Task ValidateRefreshToken_returnsNull_whenExpired()
    {
        await using ApplicationDbContext db = InMemoryDbContextFactory.Create();
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
        await sut.StoreRefreshToken(userId, tokens.RefreshToken, DateTimeOffset.UtcNow.AddDays(-1));

        (await sut.ValidateRefreshToken(tokens.RefreshToken)).Status.Should().Be(Domain.Core.Dto.Auth.RefreshTokenValidationStatus.NOT_FOUND_OR_EXPIRED);
    }

    [Fact]
    public void GenerateHubToken_ShouldReturnShortLivedTokenWithHubAudience()
    {
        using ApplicationDbContext db = InMemoryDbContextFactory.Create();
        JwtTokenService sut = CreateSut(db);
        var userId = Guid.NewGuid();

        HubTokenResponse result = sut.GenerateHubToken(userId);

        result.HubToken.Should().NotBeNullOrWhiteSpace();
        result.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(90), TimeSpan.FromSeconds(5));

        // Decode claims from the JWT payload without signature verification.
        var parts = result.HubToken.Split('.');
        parts.Should().HaveCount(3, "JWT must have three dot-separated parts");
        var payloadJson = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(PadBase64(parts[1])));
        payloadJson.Should().Contain("\"aud\":\"hub-aud\"");
        payloadJson.Should().Contain("\"token_use\":\"signalr\"");
        payloadJson.Should().NotContain("\"email\"");
        payloadJson.Should().NotContain("\"role\"");
    }

    private static string PadBase64(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
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
