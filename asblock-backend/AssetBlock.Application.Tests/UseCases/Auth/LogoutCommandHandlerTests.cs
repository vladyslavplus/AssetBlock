using AssetBlock.Application.UseCases.Auth.Logout;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Auth;

public class LogoutCommandHandlerTests
{
    private readonly IJwtTokenService _jwtTokenServiceMock;
    private readonly LogoutCommandHandler _handler;

    public LogoutCommandHandlerTests()
    {
        _jwtTokenServiceMock = Substitute.For<IJwtTokenService>();
        _handler = new LogoutCommandHandler(_jwtTokenServiceMock, NullLogger<LogoutCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenTokenValid_ShouldRevokeTokenAndReturnSuccess()
    {
        var command = new LogoutCommand("valid-token");
        var userId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();

        _jwtTokenServiceMock.ValidateRefreshToken(command.RefreshToken, Arg.Any<CancellationToken>())
            .Returns(new RefreshTokenValidationResult(RefreshTokenValidationStatus.VALID, userId, "testuser", "test@example.com", AppRoles.USER, tokenId));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _jwtTokenServiceMock.Received(1).RevokeRefreshToken(tokenId, Arg.Any<CancellationToken>());
        await _jwtTokenServiceMock.DidNotReceive().RevokeAllRefreshTokens(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTokenUnknown_ShouldReturnSuccessWithoutRevoking()
    {
        var command = new LogoutCommand("unknown-token");
        _jwtTokenServiceMock.ValidateRefreshToken(command.RefreshToken, Arg.Any<CancellationToken>())
            .Returns(new RefreshTokenValidationResult(RefreshTokenValidationStatus.NOT_FOUND_OR_EXPIRED));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _jwtTokenServiceMock.DidNotReceive().RevokeRefreshToken(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _jwtTokenServiceMock.DidNotReceive().RevokeAllRefreshTokens(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTokenAlreadyRevokedOrExpired_ShouldReturnSuccessWithoutRevoking()
    {
        var command = new LogoutCommand("revoked-or-expired-token");
        _jwtTokenServiceMock.ValidateRefreshToken(command.RefreshToken, Arg.Any<CancellationToken>())
            .Returns(new RefreshTokenValidationResult(RefreshTokenValidationStatus.NOT_FOUND_OR_EXPIRED));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _jwtTokenServiceMock.DidNotReceive().RevokeRefreshToken(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTokenValid_ShouldRevokeOnlyPresentedToken()
    {
        var command = new LogoutCommand("session-token");
        var userId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();

        _jwtTokenServiceMock.ValidateRefreshToken(command.RefreshToken, Arg.Any<CancellationToken>())
            .Returns(new RefreshTokenValidationResult(RefreshTokenValidationStatus.VALID, userId, "testuser", "test@example.com", AppRoles.USER, tokenId));

        await _handler.Handle(command, CancellationToken.None);

        await _jwtTokenServiceMock.Received(1).RevokeRefreshToken(tokenId, Arg.Any<CancellationToken>());
        await _jwtTokenServiceMock.DidNotReceive().RevokeAllRefreshTokens(userId, Arg.Any<CancellationToken>());
    }
}
