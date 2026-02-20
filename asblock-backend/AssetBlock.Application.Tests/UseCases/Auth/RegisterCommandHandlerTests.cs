using AssetBlock.Application.UseCases.Auth.Register;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
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
    private readonly RegisterCommandHandler _handler;

    public RegisterCommandHandlerTests()
    {
        _userStoreMock = Substitute.For<IUserStore>();
        _passwordHasherMock = Substitute.For<IPasswordHasher>();
        _jwtTokenServiceMock = Substitute.For<IJwtTokenService>();

        _handler = new RegisterCommandHandler(
            _userStoreMock,
            _passwordHasherMock,
            _jwtTokenServiceMock,
            NullLogger<RegisterCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenEmailExists_ShouldReturnError()
    {
        var command = new RegisterCommand("existuser", "exist@example.com", "password123");
        var existingUser = new User { Id = Guid.NewGuid(), Username = "existuser", Email = "exist@example.com", PasswordHash = "hash", Role = AppRoles.USER };

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns(existingUser);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenDbThrowsDuplicateEmailException_ShouldReturnError()
    {
        var command = new RegisterCommand("newuser", "new@example.com", "password123");

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns((User?)null);
        _passwordHasherMock.Hash(command.Password).Returns("hashed");
        _userStoreMock.Create("newuser", command.Email, "hashed", Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateEmailException());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenStoreRefreshTokenFails_ShouldRollbackUserAndThrow()
    {
        var command = new RegisterCommand("newuser", "new@example.com", "password123");
        var user = new User { Id = Guid.NewGuid(), Username = "newuser", Email = "new@example.com", PasswordHash = "hashed", Role = AppRoles.USER };
        var tokenResponse = new TokensResponse("acc", "ref", DateTimeOffset.UtcNow.AddMinutes(15), DateTimeOffset.UtcNow.AddDays(7));

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns((User?)null);
        _userStoreMock.Create("newuser", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);
        _jwtTokenServiceMock.GenerateTokenPair(user.Id, user.Username, user.Email, user.Role).Returns(tokenResponse);

        _jwtTokenServiceMock.StoreRefreshToken(user.Id, "ref", tokenResponse.RefreshExpiresAt, Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Redis unavailable"));

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("Redis unavailable");
        await _userStoreMock.Received(1).Delete(user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldReturnTokens()
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
    }
}
