using Ardalis.Result;
using AssetBlock.Application.UseCases.Auth.ConfirmEmailVerification;
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

public sealed class ConfirmEmailVerificationCommandHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IEmailActionStore _emailActionStore = Substitute.For<IEmailActionStore>();
    private readonly IEmailActionLinkProtector _linkProtector = Substitute.For<IEmailActionLinkProtector>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly ConfirmEmailVerificationCommandHandler _handler;

    public ConfirmEmailVerificationCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransaction(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new ConfirmEmailVerificationCommandHandler(
            _userStore,
            _emailActionStore,
            _linkProtector,
            _unitOfWork,
            _auditWriter,
            NullLogger<ConfirmEmailVerificationCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenTokenUnprotectFails_ShouldReturnInvalidError()
    {
        var command = new ConfirmEmailVerificationCommand("bad-token");
        // TryUnprotect returns false by default (NSubstitute default for bool)
        // No need to set up - default is false with out param as default

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        await _auditWriter.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_EMAIL_VERIFICATION &&
                e.Outcome == AuditOutcome.FAILURE),
            Arg.Any<CancellationToken>());
        await _emailActionStore.DidNotReceiveWithAnyArgs().TryConsume(Guid.Empty, default, Guid.Empty, null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenActionNotFound_ShouldReturnInvalidError()
    {
        var command = new ConfirmEmailVerificationCommand("valid-token");
        var claims = new EmailActionLinkClaims(Guid.NewGuid(), Guid.NewGuid(), EmailActionPurpose.EMAIL_VERIFICATION, DateTimeOffset.UtcNow.AddHours(1));

        _linkProtector
            .TryUnprotect(Arg.Any<string>(), Arg.Any<EmailActionPurpose>(), out Arg.Any<EmailActionLinkClaims>()!)
            .Returns(x => { x[2] = claims; return true; });
        _emailActionStore.GetById(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((EmailAction?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
    }

    [Fact]
    public async Task Handle_WhenConsumeSucceeds_ShouldSetEmailVerifiedAtAndReturnSuccess()
    {
        var user = CreateUser();
        var actionId = Guid.NewGuid();
        var version = Guid.NewGuid();
        var claims = new EmailActionLinkClaims(actionId, version, EmailActionPurpose.EMAIL_VERIFICATION, DateTimeOffset.UtcNow.AddHours(1));
        var action = new EmailAction
        {
            Id = actionId, UserId = user.Id, Purpose = EmailActionPurpose.EMAIL_VERIFICATION,
            TargetEmail = user.Email, Version = version,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        };

        _linkProtector
            .TryUnprotect(Arg.Any<string>(), Arg.Any<EmailActionPurpose>(), out Arg.Any<EmailActionLinkClaims>()!)
            .Returns(x => { x[2] = claims; return true; });
        _emailActionStore.GetById(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetByIdForUpdate(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);
        _emailActionStore.TryConsume(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _handler.Handle(new ConfirmEmailVerificationCommand("token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.EmailVerifiedAt.Should().NotBeNull();
        await _userStore.Received(1).Update(user, Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_EMAIL_VERIFICATION &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == user.Id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTryConsumeReturnsFalse_ShouldReturnInvalidError()
    {
        var user = CreateUser();
        var actionId = Guid.NewGuid();
        var version = Guid.NewGuid();
        var claims = new EmailActionLinkClaims(actionId, version, EmailActionPurpose.EMAIL_VERIFICATION, DateTimeOffset.UtcNow.AddHours(1));
        var action = new EmailAction
        {
            Id = actionId, UserId = user.Id, Purpose = EmailActionPurpose.EMAIL_VERIFICATION,
            TargetEmail = user.Email, Version = version,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        };

        _linkProtector
            .TryUnprotect(Arg.Any<string>(), Arg.Any<EmailActionPurpose>(), out Arg.Any<EmailActionLinkClaims>()!)
            .Returns(x => { x[2] = claims; return true; });
        _emailActionStore.GetById(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetByIdForUpdate(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);
        _emailActionStore.TryConsume(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _handler.Handle(new ConfirmEmailVerificationCommand("token"), CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        user.EmailVerifiedAt.Should().BeNull();
    }

    private static User CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = "confirmuser",
        Email = "confirm@example.test",
        PasswordHash = "hash",
        Role = AppRoles.USER
    };
}
