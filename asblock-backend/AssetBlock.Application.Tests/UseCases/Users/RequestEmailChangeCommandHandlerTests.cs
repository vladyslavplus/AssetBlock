using Ardalis.Result;
using AssetBlock.Application.UseCases.Users.RequestEmailChange;
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

public sealed class RequestEmailChangeCommandHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly IEmailActionStore _emailActionStore = Substitute.For<IEmailActionStore>();
    private readonly IOutboxStore _outboxStore = Substitute.For<IOutboxStore>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly RequestEmailChangeCommandHandler _handler;

    public RequestEmailChangeCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransaction(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new RequestEmailChangeCommandHandler(
            _userStore,
            _passwordHasher,
            _emailActionStore,
            _outboxStore,
            _unitOfWork,
            _auditWriter,
            NullLogger<RequestEmailChangeCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCurrentPasswordInvalid_ShouldReturnInvalidError()
    {
        var user = CreateUser();
        var command = new RequestEmailChangeCommand(user.Id, "new@example.test", "wrong-pass");
        _userStore.GetByIdForUpdate(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify(command.CurrentPassword, user.PasswordHash).Returns(false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_AUTH_CURRENT_PASSWORD_INVALID);
        await _auditWriter.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_EMAIL_CHANGE_REQUEST &&
                e.Outcome == AuditOutcome.FAILURE &&
                e.ResourceId == user.Id.ToString()),
            Arg.Any<CancellationToken>());
        await _emailActionStore.DidNotReceiveWithAnyArgs().IssueOrReplace(Guid.Empty, default, null!, TimeSpan.Zero, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenNewEmailSameAsCurrent_ShouldReturnSameEmailError()
    {
        var user = CreateUser();
        var command = new RequestEmailChangeCommand(user.Id, user.Email.ToUpper(), "pass");
        _userStore.GetByIdForUpdate(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify(command.CurrentPassword, user.PasswordHash).Returns(true);
        _userStore.GetByEmail(user.Email, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_EMAIL_CHANGE_SAME_AS_CURRENT);
        await _emailActionStore.DidNotReceiveWithAnyArgs().IssueOrReplace(Guid.Empty, default, null!, TimeSpan.Zero, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenNewEmailTakenByOtherUser_ShouldReturnConflict()
    {
        var user = CreateUser();
        var otherUser = CreateUser("other", "other@example.test");
        var command = new RequestEmailChangeCommand(user.Id, "other@example.test", "pass");
        _userStore.GetByIdForUpdate(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _userStore.GetByEmail(Arg.Is<string>(e => e == "other@example.test"), Arg.Any<CancellationToken>()).Returns(otherUser);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_EMAIL_CHANGE_TARGET_TAKEN);
    }

    [Fact]
    public async Task Handle_WhenValid_ShouldIssueEmailChangeActionAndEnqueueOutbox()
    {
        var user = CreateUser();
        const string newEmail = "newemail@example.test";
        var userId = user.Id;
        var command = new RequestEmailChangeCommand(userId, newEmail, "pass");
        var action = new EmailAction
        {
            Id = Guid.NewGuid(), UserId = userId, Purpose = EmailActionPurpose.EMAIL_CHANGE,
            TargetEmail = newEmail, Version = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };

        _userStore.GetByIdForUpdate(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _userStore.GetByEmail(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        _emailActionStore.GetCurrent(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<CancellationToken>())
            .Returns((EmailAction?)null);
        _emailActionStore.IsInCooldown(Arg.Any<EmailAction?>(), Arg.Any<TimeSpan>(), Arg.Any<DateTimeOffset>()).Returns(false);
        _emailActionStore.IssueOrReplace(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(action);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Email.Should().Be("user@example.test", because: "Email must NOT be updated until confirmation");
        await _emailActionStore.Received(1).IssueOrReplace(
            Arg.Any<Guid>(),
            Arg.Is(EmailActionPurpose.EMAIL_CHANGE),
            Arg.Is(newEmail),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        await _outboxStore.Received(1).Enqueue(
            OutboxMessageTypes.EMAIL_ACTION_DISPATCH,
            Arg.Is<EmailActionDispatchPayload>(p =>
                p.EmailActionId == action.Id &&
                p.ActionVersion == action.Version &&
                p.RecipientUserId == userId &&
                p.TemplateKind == EmailTemplateKind.EMAIL_CHANGE_CONFIRMATION),
            Arg.Any<CancellationToken>());
    }

    private static User CreateUser(string username = "changeuser", string email = "user@example.test") => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        Email = email,
        PasswordHash = "hash",
        Role = AppRoles.USER
    };
}
