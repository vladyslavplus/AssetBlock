using AssetBlock.Application.UseCases.Auth.Login;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.Api;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Auth;

public class LoginCommandHandlerTests
{
    private readonly IUserStore _userStoreMock;
    private readonly IPasswordHasher _passwordHasherMock;
    private readonly IJwtTokenService _jwtTokenServiceMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly LoginCommandHandler _handler;

    public LoginCommandHandlerTests()
    {
        _userStoreMock = Substitute.For<IUserStore>();
        _passwordHasherMock = Substitute.For<IPasswordHasher>();
        _jwtTokenServiceMock = Substitute.For<IJwtTokenService>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new LoginCommandHandler(
            _userStoreMock,
            _passwordHasherMock,
            _jwtTokenServiceMock,
            _unitOfWorkMock,
            _auditWriterMock,
            NullLogger<LoginCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenUserNotFound_ShouldReturnErrorAndWriteAnonymousFailureAudit()
    {
        var command = new LoginCommand("test@example.com", "password123");
        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS);
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_LOGIN &&
                e.Outcome == AuditOutcome.FAILURE &&
                e.ActorTypeOverride == AuditActorType.ANONYMOUS &&
                e.Metadata != null && e.Metadata.ContainsKey("reasonCode")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPasswordIsInvalid_ShouldReturnSameFailureAuditAsUnknownEmail()
    {
        var command = new LoginCommand("test@example.com", "wrong-password");
        var user = new User { Id = Guid.NewGuid(), Username = "testuser", Email = "test@example.com", PasswordHash = "hashed", Role = AppRoles.USER };

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasherMock.Verify(command.Password, user.PasswordHash).Returns(false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS);
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_LOGIN &&
                e.Outcome == AuditOutcome.FAILURE &&
                e.ActorTypeOverride == AuditActorType.ANONYMOUS &&
                e.Metadata != null && (string?)e.Metadata["reasonCode"] == ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCredentialsValid_ShouldReturnTokensAndWriteAuditInsideTransaction()
    {
        var command = new LoginCommand("test@example.com", "valid-password");
        var user = new User { Id = Guid.NewGuid(), Username = "testuser", Email = "test@example.com", PasswordHash = "hashed", Role = AppRoles.USER };
        var tokenResponse = new TokensResponse("acc", "ref", DateTimeOffset.UtcNow.AddMinutes(15), DateTimeOffset.UtcNow.AddDays(7));

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasherMock.Verify(command.Password, user.PasswordHash).Returns(true);
        _jwtTokenServiceMock.GenerateTokenPair(user.Id, user.Username, user.Email, user.Role).Returns(tokenResponse);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("acc");
        result.Value.RefreshToken.Should().Be("ref");

        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _jwtTokenServiceMock.Received(1).StoreRefreshToken(user.Id, "ref", tokenResponse.RefreshExpiresAt, Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_LOGIN &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ActorTypeOverride == AuditActorType.USER &&
                e.ActorUserIdOverride == user.Id),
            Arg.Any<CancellationToken>());
    }
}
