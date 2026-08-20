using Ardalis.Result;
using AssetBlock.Application.UseCases.Users.ChangePassword;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public sealed class ChangePasswordCommandHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly IJwtTokenService _jwtTokenService = Substitute.For<IJwtTokenService>();
    private readonly IOutboxStore _outboxStore = Substitute.For<IOutboxStore>();
    private readonly ITransactionalEmailComposer _emailComposer = Substitute.For<ITransactionalEmailComposer>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly ChangePasswordCommandHandler _handler;

    public ChangePasswordCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransaction(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new ChangePasswordCommandHandler(
            _userStore,
            _passwordHasher,
            _jwtTokenService,
            _outboxStore,
            _emailComposer,
            _unitOfWork,
            _auditWriter,
            NullLogger<ChangePasswordCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCurrentPasswordIsInvalid_ShouldWriteBestEffortFailure()
    {
        var command = new ChangePasswordCommand(Guid.NewGuid(), "wrong", "new-password");
        var user = CreateUser(command.UserId);
        _userStore.GetByIdForUpdate(command.UserId, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify(command.CurrentPassword, user.PasswordHash).Returns(false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e =>
            e.Identifier == ErrorCodes.ERR_AUTH_CURRENT_PASSWORD_INVALID);
        await _auditWriter.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.USER_PASSWORD_CHANGE
                && e.Outcome == AuditOutcome.FAILURE
                && e.ResourceId == command.UserId.ToString()
                && e.Metadata != null
                && e.Metadata.ContainsKey("reasonCode")),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().ExecuteInTransaction(
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenValid_ShouldUpdatePasswordRevokesTokensEnqueuesEmailAndWritesAudit()
    {
        var command = new ChangePasswordCommand(Guid.NewGuid(), "current", "new-password");
        var user = CreateUser(command.UserId);
        var notice = new EmailDispatchPayload(user.Email, user.Id, EmailTemplateKind.PASSWORD_CHANGED_NOTICE, "Password changed", "text", "<p>html</p>");

        _userStore.GetByIdForUpdate(command.UserId, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify(command.CurrentPassword, user.PasswordHash).Returns(true);
        _passwordHasher.Hash(command.NewPassword).Returns("new-hash");
        _emailComposer.CreatePasswordChangedNotice(user.Email, user.Id).Returns(notice);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.PasswordHash.Should().Be("new-hash");
        await _userStore.Received(1).Update(user, Arg.Any<CancellationToken>());
        await _jwtTokenService.Received(1).RevokeAllRefreshTokens(user.Id, Arg.Any<CancellationToken>());
        await _outboxStore.Received(1).Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            Arg.Is<EmailDispatchPayload>(p =>
                p.RecipientUserId == user.Id &&
                p.TemplateKind == EmailTemplateKind.PASSWORD_CHANGED_NOTICE),
            Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.USER_PASSWORD_CHANGE
                && e.Outcome == AuditOutcome.SUCCESS
                && e.ResourceId == command.UserId.ToString()),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).ExecuteInTransaction(
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    private static User CreateUser(Guid id) => new()
    {
        Id = id,
        Username = "user",
        Email = "user@example.test",
        PasswordHash = "old-hash",
        Role = AppRoles.USER
    };
}
