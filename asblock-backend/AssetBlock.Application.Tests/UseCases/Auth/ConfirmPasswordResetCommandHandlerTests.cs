using AssetBlock.Application.UseCases.Auth.ConfirmPasswordReset;
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

public sealed class ConfirmPasswordResetCommandHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IEmailActionStore _emailActionStore = Substitute.For<IEmailActionStore>();
    private readonly IEmailActionLinkProtector _linkProtector = Substitute.For<IEmailActionLinkProtector>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly IJwtTokenService _jwtTokenService = Substitute.For<IJwtTokenService>();
    private readonly IOutboxStore _outboxStore = Substitute.For<IOutboxStore>();
    private readonly ITransactionalEmailComposer _emailComposer = Substitute.For<ITransactionalEmailComposer>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly ConfirmPasswordResetCommandHandler _handler;

    public ConfirmPasswordResetCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransaction(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        _passwordHasher.Hash(Arg.Any<string>()).Returns("hashed");
        _emailComposer.CreatePasswordChangedNotice(Arg.Any<string>(), Arg.Any<Guid>())
            .Returns(ci => new EmailDispatchPayload(
                ci.ArgAt<string>(0),
                ci.ArgAt<Guid>(1),
                EmailTemplateKind.PASSWORD_CHANGED_NOTICE,
                "Password changed",
                "text",
                "<p>html</p>"));

        _handler = new ConfirmPasswordResetCommandHandler(
            _userStore,
            _emailActionStore,
            _linkProtector,
            _passwordHasher,
            _jwtTokenService,
            _outboxStore,
            _emailComposer,
            _unitOfWork,
            _auditWriter,
            NullLogger<ConfirmPasswordResetCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenUnverifiedUserResetsPassword_ShouldSetEmailVerifiedAt()
    {
        var user = CreateUser(emailVerifiedAt: null);
        SetupSuccessfulConsume(user);

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand("token", "NewPassword1!"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.EmailVerifiedAt.Should().NotBeNull();
        await _auditWriter.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_PASSWORD_RESET_CONFIRM &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.Metadata != null &&
                e.Metadata.ContainsKey("emailVerifiedByPasswordReset") &&
                Equals(e.Metadata["emailVerifiedByPasswordReset"], true)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAlreadyVerified_ShouldKeepEmailVerifiedAtAndOmitResetFlag()
    {
        var verifiedAt = DateTimeOffset.Parse("2026-01-15T12:00:00Z");
        var user = CreateUser(emailVerifiedAt: verifiedAt);
        SetupSuccessfulConsume(user);

        var result = await _handler.Handle(
            new ConfirmPasswordResetCommand("token", "NewPassword1!"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.EmailVerifiedAt.Should().Be(verifiedAt);
        await _auditWriter.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.AUTH_PASSWORD_RESET_CONFIRM &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.Metadata == null),
            Arg.Any<CancellationToken>());
    }

    private void SetupSuccessfulConsume(User user)
    {
        var actionId = Guid.NewGuid();
        var version = Guid.NewGuid();
        var claims = new EmailActionLinkClaims(
            actionId,
            version,
            EmailActionPurpose.PASSWORD_RESET,
            DateTimeOffset.UtcNow.AddMinutes(30));
        var action = new EmailAction
        {
            Id = actionId,
            UserId = user.Id,
            Purpose = EmailActionPurpose.PASSWORD_RESET,
            TargetEmail = user.Email,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };

        _linkProtector
            .TryUnprotect(Arg.Any<string>(), Arg.Any<EmailActionPurpose>(), out Arg.Any<EmailActionLinkClaims>()!)
            .Returns(x =>
            {
                x[2] = claims;
                return true;
            });
        _emailActionStore.GetById(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(action);
        _userStore.GetByIdForUpdate(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);
        _emailActionStore
            .TryConsume(Arg.Any<Guid>(), Arg.Any<EmailActionPurpose>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private static User CreateUser(DateTimeOffset? emailVerifiedAt) => new()
    {
        Id = Guid.NewGuid(),
        Username = "resetuser",
        Email = "reset@example.test",
        PasswordHash = "old-hash",
        Role = AppRoles.USER,
        EmailVerifiedAt = emailVerifiedAt
    };
}
