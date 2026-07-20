using Ardalis.Result;
using AssetBlock.Application.UseCases.Auth.ResendEmailVerification;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Auth;

public sealed class ResendEmailVerificationCommandHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IEmailActionStore _emailActionStore = Substitute.For<IEmailActionStore>();
    private readonly IOutboxStore _outboxStore = Substitute.For<IOutboxStore>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ResendEmailVerificationCommandHandler _handler;

    public ResendEmailVerificationCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransaction(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new ResendEmailVerificationCommandHandler(
            _userStore,
            _emailActionStore,
            _outboxStore,
            _unitOfWork,
            NullLogger<ResendEmailVerificationCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenUserNotFound_ShouldReturnNotFound()
    {
        var command = new ResendEmailVerificationCommand(Guid.NewGuid());
        _userStore.GetByIdForUpdate(command.UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        await _emailActionStore.DidNotReceiveWithAnyArgs().IssueOrReplace(Guid.Empty, default, null!, TimeSpan.Zero, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenAlreadyVerified_ShouldReturnSuccessWithoutAction()
    {
        var user = CreateUser(emailVerifiedAt: DateTimeOffset.UtcNow);
        var command = new ResendEmailVerificationCommand(user.Id);
        _userStore.GetByIdForUpdate(command.UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _emailActionStore.DidNotReceiveWithAnyArgs().IssueOrReplace(Guid.Empty, default, null!, TimeSpan.Zero, CancellationToken.None);
        await _outboxStore.DidNotReceiveWithAnyArgs().Enqueue(null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenInCooldown_ShouldReturnCooldownError()
    {
        var user = CreateUser();
        var command = new ResendEmailVerificationCommand(user.Id);
        var existingAction = new EmailAction
        {
            Id = Guid.NewGuid(), UserId = user.Id, Purpose = EmailActionPurpose.EMAIL_VERIFICATION,
            TargetEmail = user.Email, Version = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            LastSentAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        };

        _userStore.GetByIdForUpdate(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);
        _emailActionStore.GetCurrent(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(existingAction);
        _emailActionStore.IsInCooldown(Arg.Any<EmailAction?>(), Arg.Any<TimeSpan>(), Arg.Any<DateTimeOffset>()).Returns(true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_EMAIL_ACTION_COOLDOWN);
        await _emailActionStore.DidNotReceiveWithAnyArgs().IssueOrReplace(Guid.Empty, EmailActionPurpose.EMAIL_VERIFICATION, null!, TimeSpan.Zero, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenAllowed_ShouldReissueActionAndEnqueueOutbox()
    {
        var user = CreateUser();
        var command = new ResendEmailVerificationCommand(user.Id);
        var newAction = new EmailAction
        {
            Id = Guid.NewGuid(), UserId = user.Id, Purpose = EmailActionPurpose.EMAIL_VERIFICATION,
            TargetEmail = user.Email, Version = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        };

        _userStore.GetByIdForUpdate(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);
        _emailActionStore.GetCurrent(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<CancellationToken>())
            .Returns((EmailAction?)null);
        _emailActionStore.IsInCooldown(Arg.Any<EmailAction?>(), Arg.Any<TimeSpan>(), Arg.Any<DateTimeOffset>()).Returns(false);
        _emailActionStore.IssueOrReplace(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(newAction);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _emailActionStore.Received(1).IssueOrReplace(
            user.Id,
            EmailActionPurpose.EMAIL_VERIFICATION,
            user.Email,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        await _outboxStore.Received(1).Enqueue(
            OutboxMessageTypes.EMAIL_ACTION_DISPATCH,
            Arg.Is<EmailActionDispatchPayload>(p =>
                p.EmailActionId == newAction.Id &&
                p.ActionVersion == newAction.Version &&
                p.RecipientUserId == user.Id &&
                p.TemplateKind == EmailTemplateKind.EMAIL_VERIFICATION),
            Arg.Any<CancellationToken>());
    }

    private static User CreateUser(DateTimeOffset? emailVerifiedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Username = "verifyuser",
        Email = "verify@example.test",
        PasswordHash = "hash",
        Role = AppRoles.USER,
        EmailVerifiedAt = emailVerifiedAt
    };
}
