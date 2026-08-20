using AssetBlock.Application.UseCases.Auth.RequestPasswordReset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Auth;

public sealed class RequestPasswordResetCommandHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IEmailActionStore _emailActionStore = Substitute.For<IEmailActionStore>();
    private readonly IOutboxStore _outboxStore = Substitute.For<IOutboxStore>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly RequestPasswordResetCommandHandler _handler;

    public RequestPasswordResetCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransaction(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new RequestPasswordResetCommandHandler(
            _userStore,
            _emailActionStore,
            _outboxStore,
            _unitOfWork,
            _auditWriter,
            NullLogger<RequestPasswordResetCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenEmailUnknown_ShouldReturnSuccessWithoutIssuingAction()
    {
        var command = new RequestPasswordResetCommand("unknown@example.test");
        _userStore.GetByEmail(command.Email.Trim(), Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _emailActionStore.DidNotReceiveWithAnyArgs().IssueOrReplace(Guid.Empty, default, null!, TimeSpan.Zero, CancellationToken.None);
        await _outboxStore.DidNotReceiveWithAnyArgs().Enqueue(null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenUserInCooldown_ShouldReturnSuccessWithoutIssuingAction()
    {
        var user = CreateUser();
        var command = new RequestPasswordResetCommand(user.Email);
        var existingAction = new EmailAction
        {
            Id = Guid.NewGuid(), UserId = user.Id, Purpose = EmailActionPurpose.PASSWORD_RESET,
            TargetEmail = user.Email, Version = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            LastSentAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        };

        _userStore.GetByEmail(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);
        _emailActionStore.GetCurrent(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(existingAction);
        _emailActionStore.IsInCooldown(Arg.Any<EmailAction?>(), Arg.Any<TimeSpan>(), Arg.Any<DateTimeOffset>()).Returns(true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _emailActionStore.DidNotReceiveWithAnyArgs().IssueOrReplace(Guid.Empty, default, null!, TimeSpan.Zero, CancellationToken.None);
        await _outboxStore.DidNotReceiveWithAnyArgs().Enqueue(null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenKnownEmailNotInCooldown_ShouldIssueActionAndEnqueueOutbox()
    {
        var user = CreateUser();
        var command = new RequestPasswordResetCommand(user.Email);
        var action = new EmailAction
        {
            Id = Guid.NewGuid(), UserId = user.Id, Purpose = EmailActionPurpose.PASSWORD_RESET,
            TargetEmail = user.Email, Version = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };

        _userStore.GetByEmail(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);
        _emailActionStore.GetCurrent(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<CancellationToken>())
            .Returns((EmailAction?)null);
        _emailActionStore.IsInCooldown(Arg.Any<EmailAction?>(), Arg.Any<TimeSpan>(), Arg.Any<DateTimeOffset>()).Returns(false);
        _emailActionStore.IssueOrReplace(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(action);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _emailActionStore.Received(1).IssueOrReplace(
            user.Id,
            EmailActionPurpose.PASSWORD_RESET,
            user.Email,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        await _outboxStore.Received(1).Enqueue(
            OutboxMessageTypes.EMAIL_ACTION_DISPATCH,
            Arg.Is<EmailActionDispatchPayload>(p =>
                p.EmailActionId == action.Id &&
                p.ActionVersion == action.Version &&
                p.RecipientUserId == user.Id &&
                p.TemplateKind == EmailTemplateKind.PASSWORD_RESET),
            Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_PASSWORD_RESET_REQUEST &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == user.Id.ToString() &&
                e.ActorTypeOverride == AuditActorType.ANONYMOUS &&
                e.ActorUserIdOverride == null),
            Arg.Any<CancellationToken>());
    }

    private static User CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = "resetuser",
        Email = "reset@example.test",
        PasswordHash = "hash",
        Role = AppRoles.USER
    };
}
