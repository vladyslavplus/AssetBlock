using AssetBlock.Application.UseCases.Auth.RefreshToken;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.Api;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Auth;

public class RefreshTokenCommandHandlerTests
{
    private readonly IJwtTokenService _jwtTokenServiceMock;
    private readonly RefreshTokenCommandHandler _handler;

    public RefreshTokenCommandHandlerTests()
    {
        _jwtTokenServiceMock = Substitute.For<IJwtTokenService>();
        _handler = new RefreshTokenCommandHandler(_jwtTokenServiceMock, NullLogger<RefreshTokenCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenTokenInvalid_ShouldReturnError()
    {
        var command = new RefreshTokenCommand("invalid-token");
        _jwtTokenServiceMock.ValidateRefreshToken(command.RefreshToken, Arg.Any<CancellationToken>())
            .Returns(((Guid, string, string, string, Guid)?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_AUTH_TOKEN_INVALID);
    }

    [Fact]
    public async Task Handle_WhenTokenValid_ShouldRevokeOldStoreNewAndReturnTokens()
    {
        var command = new RefreshTokenCommand("old-valid-token");
        var userId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();
        const string email = "test@example.com";
        const string username = "testuser";
        const string role = AppRoles.USER;

        var tokenResponse = new TokensResponse("new-acc", "new-ref", DateTimeOffset.UtcNow.AddMinutes(15), DateTimeOffset.UtcNow.AddDays(7));

        _jwtTokenServiceMock.ValidateRefreshToken(command.RefreshToken, Arg.Any<CancellationToken>())
            .Returns((userId, username, email, role, tokenId));
        _jwtTokenServiceMock.GenerateTokenPair(userId, username, email, role).Returns(tokenResponse);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("new-acc");
        result.Value.RefreshToken.Should().Be("new-ref");

        // Asserts
        await _jwtTokenServiceMock.Received(1).RevokeRefreshToken(tokenId, Arg.Any<CancellationToken>());
        await _jwtTokenServiceMock.Received(1).StoreRefreshToken(userId, "new-ref", tokenResponse.RefreshExpiresAt, Arg.Any<CancellationToken>());
    }
}
