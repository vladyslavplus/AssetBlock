using Ardalis.Result;
using AssetBlock.Application.UseCases.Auth.Register;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Primitives.Api;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Auth;

public class RegisterCommandHandlerTests
{
    private readonly IUserStore _userStoreMock;
    private readonly IPasswordHasher _passwordHasherMock;
    private readonly IJwtTokenService _jwtTokenServiceMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly RegisterCommandHandler _handler;

    public RegisterCommandHandlerTests()
    {
        _userStoreMock = Substitute.For<IUserStore>();
        _passwordHasherMock = Substitute.For<IPasswordHasher>();
        _jwtTokenServiceMock = Substitute.For<IJwtTokenService>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new RegisterCommandHandler(
            _userStoreMock,
            _passwordHasherMock,
            _jwtTokenServiceMock,
            _unitOfWorkMock,
            _auditWriterMock,
            NullLogger<RegisterCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenEmailExists_ShouldReturnConflictAndWriteBestEffort()
    {
        var command = new RegisterCommand("existuser", "exist@example.com", "password123");
        var existingUser = new User { Id = Guid.NewGuid(), Username = "existuser", Email = "exist@example.com", PasswordHash = "hash", Role = AppRoles.USER };

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns(existingUser);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS);
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_REGISTER &&
                e.Outcome == AuditOutcome.FAILURE &&
                e.ActorTypeOverride == AuditActorType.ANONYMOUS &&
                e.Metadata != null && e.Metadata.ContainsKey("reasonCode")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDbThrowsDuplicateEmailException_ShouldReturnConflict()
    {
        var command = new RegisterCommand("newuser", "new@example.com", "password123");

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns((User?)null);
        _passwordHasherMock.Hash(command.Password).Returns("hashed");
        _userStoreMock.Create("newuser", command.Email, "hashed", Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateEmailException());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenDbThrowsDuplicateUsernameException_ShouldReturnConflict()
    {
        var command = new RegisterCommand("taken", "new@example.com", "password123");

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns((User?)null);
        _passwordHasherMock.Hash(command.Password).Returns("hashed");
        _userStoreMock.Create("taken", command.Email, "hashed", Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateUsernameException());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_USERNAME_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldReturnTokensAndWriteAuditInsideTransaction()
    {
        var command = new RegisterCommand("newuser", "new@example.com", "password123");
        var user = new User { Id = Guid.NewGuid(), Username = "newuser", Email = "new@example.com", PasswordHash = "hashed", Role = AppRoles.USER };
        var tokenResponse = new TokensResponse("acc", "ref", DateTimeOffset.UtcNow.AddMinutes(15), DateTimeOffset.UtcNow.AddDays(7));

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns((User?)null);
        _passwordHasherMock.Hash(command.Password).Returns("hashed");
        _userStoreMock.Create("newuser", command.Email, "hashed", Arg.Any<CancellationToken>()).Returns(user);
        _jwtTokenServiceMock.GenerateTokenPair(user.Id, user.Username, user.Email, user.Role).Returns(tokenResponse);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("acc");
        result.Value.RefreshToken.Should().Be("ref");

        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_REGISTER &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceType == AuditResourceTypes.USER &&
                e.ResourceId == user.Id.ToString() &&
                e.ActorTypeOverride == AuditActorType.USER &&
                e.ActorUserIdOverride == user.Id),
            Arg.Any<CancellationToken>());
    }
}
