using Ardalis.Result;
using AssetBlock.Application.UseCases.Auth.Register;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Auth;

public class RegisterCommandHandlerTests
{
    private readonly IUserStore _userStoreMock;
    private readonly IPasswordHasher _passwordHasherMock;
    private readonly IEmailActionStore _emailActionStoreMock;
    private readonly IOutboxStore _outboxStoreMock;
    private readonly ITransactionalEmailComposer _emailComposerMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly RegisterCommandHandler _handler;

    public RegisterCommandHandlerTests()
    {
        _userStoreMock = Substitute.For<IUserStore>();
        _passwordHasherMock = Substitute.For<IPasswordHasher>();
        _emailActionStoreMock = Substitute.For<IEmailActionStore>();
        _outboxStoreMock = Substitute.For<IOutboxStore>();
        _emailComposerMock = Substitute.For<ITransactionalEmailComposer>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new RegisterCommandHandler(
            _userStoreMock,
            _passwordHasherMock,
            _emailActionStoreMock,
            _outboxStoreMock,
            _emailComposerMock,
            _unitOfWorkMock,
            _auditWriterMock,
            NullLogger<RegisterCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenEmailExists_ShouldReturnSuccessAndQueueSecurityNotice()
    {
        var command = new RegisterCommand("existuser", "exist@example.com", "password123");
        var existingUser = new User { Id = Guid.NewGuid(), Username = "existuser", Email = "exist@example.com", PasswordHash = "hash", Role = AppRoles.USER };

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns(existingUser);
        var notice = new EmailDispatchPayload(
            existingUser.Email,
            existingUser.Id,
            EmailTemplateKind.REGISTRATION_ATTEMPT_NOTICE,
            "subject",
            "text",
            "html");
        _emailComposerMock.CreateRegistrationAttemptNotice(existingUser.Email, existingUser.Id).Returns(notice);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.EMAIL_DISPATCH,
            notice,
            Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_REGISTER &&
                e.Outcome == AuditOutcome.FAILURE &&
                e.ActorTypeOverride == AuditActorType.ANONYMOUS &&
                e.Metadata != null && e.Metadata.ContainsKey("reasonCode")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDbThrowsDuplicateEmailException_ShouldReturnSuccess()
    {
        var command = new RegisterCommand("newuser", "new@example.com", "password123");

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns((User?)null);
        _passwordHasherMock.Hash(command.Password).Returns("hashed");
        _userStoreMock.Create("newuser", command.Email, "hashed", Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateEmailException());
        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>())
            .Returns(null, new User
            {
                Id = Guid.NewGuid(),
                Username = "existing",
                Email = command.Email,
                PasswordHash = "hash",
                Role = AppRoles.USER
            });

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenDbThrowsDuplicateUsernameException_ShouldReturnConflict()
    {
        var command = new RegisterCommand("taken", "new@example.com", "password123");

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns((User?)null);
        _passwordHasherMock.Hash(command.Password).Returns("hashed");
        _userStoreMock.Create("taken", command.Email, "hashed", Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateUsernameException());

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_USERNAME_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldReturnTokensAndWriteAuditInsideTransaction()
    {
        var command = new RegisterCommand("newuser", "new@example.com", "password123");
        var user = new User { Id = Guid.NewGuid(), Username = "newuser", Email = "new@example.com", PasswordHash = "hashed", Role = AppRoles.USER };
        var action = new EmailAction { Id = Guid.NewGuid(), UserId = user.Id, Purpose = EmailActionPurpose.EMAIL_VERIFICATION, TargetEmail = user.Email, Version = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };

        _userStoreMock.GetByEmail(command.Email, Arg.Any<CancellationToken>()).Returns((User?)null);
        _passwordHasherMock.Hash(command.Password).Returns("hashed");
        _userStoreMock.Create("newuser", command.Email, "hashed", Arg.Any<CancellationToken>()).Returns(user);
        _emailActionStoreMock.IssueOrReplace(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(action);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _emailActionStoreMock.Received(1).IssueOrReplace(
            user.Id,
            EmailActionPurpose.EMAIL_VERIFICATION,
            user.Email,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.EMAIL_ACTION_DISPATCH,
            Arg.Is<EmailActionDispatchPayload>(p =>
                p.EmailActionId == action.Id &&
                p.ActionVersion == action.Version &&
                p.RecipientUserId == user.Id &&
                p.TemplateKind == EmailTemplateKind.EMAIL_VERIFICATION),
            Arg.Any<CancellationToken>());
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
