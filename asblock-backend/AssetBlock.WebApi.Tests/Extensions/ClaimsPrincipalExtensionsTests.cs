using System.Security.Claims;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.Extensions;
using AwesomeAssertions;

namespace AssetBlock.WebApi.Tests.Extensions;

public sealed class ClaimsPrincipalExtensionsTests
{
    private static readonly Guid _expected = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "TestAuthType"));

    [Fact]
    public void TryGetUserId_WhenNameIdentifierValid_ShouldReturnExpectedId()
    {
        var principal = Principal(new Claim(ClaimTypes.NameIdentifier, _expected.ToString()));

        var result = principal.TryGetUserId(out var userId);

        result.Should().BeTrue();
        userId.Should().Be(_expected);
    }

    [Fact]
    public void TryGetUserId_WhenNameIdentifierMissingAndSubValid_ShouldReturnExpectedId()
    {
        var principal = Principal(new Claim(JwtClaimTypes.SUB, _expected.ToString()));

        var result = principal.TryGetUserId(out var userId);

        result.Should().BeTrue();
        userId.Should().Be(_expected);
    }

    [Fact]
    public void TryGetUserId_WhenBothClaimsMissing_ShouldReturnFalse()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var result = principal.TryGetUserId(out Guid _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetUserId_WhenSelectedClaimMalformed_ShouldReturnFalse()
    {
        var principal = Principal(new Claim(ClaimTypes.NameIdentifier, "not-a-guid"));

        var result = principal.TryGetUserId(out Guid _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetUserId_WhenBothClaimsPresent_ShouldPreferNameIdentifier()
    {
        var other = Guid.NewGuid();
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, _expected.ToString()),
            new Claim(JwtClaimTypes.SUB, other.ToString()));

        var result = principal.TryGetUserId(out var userId);

        result.Should().BeTrue();
        userId.Should().Be(_expected);
    }

    [Fact]
    public void TryGetUserId_WhenNameIdentifierMalformedAndSubValid_ShouldNotFallBack()
    {
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, "not-a-guid"),
            new Claim(JwtClaimTypes.SUB, _expected.ToString()));

        var result = principal.TryGetUserId(out Guid _);

        result.Should().BeFalse();
    }

    [Fact]
    public void GetUserIdOrNull_WhenUserIdResolves_ShouldMatchTryGetUserId()
    {
        var principal = Principal(new Claim(ClaimTypes.NameIdentifier, _expected.ToString()));

        var viaNull = principal.GetUserIdOrNull();

        principal.TryGetUserId(out var viaTry).Should().BeTrue();
        viaNull.Should().Be(viaTry);
    }

    [Fact]
    public void GetUserIdOrNull_WhenNoClaimResolves_ShouldReturnNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        principal.GetUserIdOrNull().Should().BeNull();
    }

    [Fact]
    public void TryGetUserId_WhenPrincipalIsEmpty_ShouldNotThrow()
    {
        var principal = new ClaimsPrincipal();

        var action = () => principal.TryGetUserId(out _);

        action.Should().NotThrow();
    }
}
