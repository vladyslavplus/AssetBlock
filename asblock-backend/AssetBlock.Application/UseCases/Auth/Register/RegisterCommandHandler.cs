using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.Register;

internal sealed class RegisterCommandHandler(
    IUserStore userStore,
    IPasswordHasher passwordHasher,
    IEmailActionStore emailActionStore,
    IOutboxStore outboxStore,
    ITransactionalEmailComposer emailComposer,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<RegisterCommandHandler> logger) : IRequestHandler<RegisterCommand, Result>
{
    public async Task<Result> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        User? existing = await userStore.GetByEmail(request.Email, cancellationToken);
        if (existing is not null)
        {
            _ = passwordHasher.Hash(request.Password);
            await RecordExistingAccountAttempt(existing, cancellationToken);
            return Result.Success();
        }

        var hash = passwordHasher.Hash(request.Password);
        User? user = null;

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                user = await userStore.Create(request.Username, request.Email, hash, ct);

                EmailAction action = await emailActionStore.IssueOrReplace(
                    user.Id,
                    EmailActionPurpose.EMAIL_VERIFICATION,
                    user.Email,
                    EmailActionConstants.VerificationExpiry,
                    ct);
                await outboxStore.Enqueue(
                    OutboxMessageTypes.EMAIL_ACTION_DISPATCH,
                    new EmailActionDispatchPayload(action.Id, action.Version, user.Id, EmailTemplateKind.EMAIL_VERIFICATION),
                    ct);

                await auditWriter.Write(new AuditEvent(
                    AuditActions.AUTH_REGISTER,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.USER,
                    user.Id.ToString(),
                    ActorTypeOverride: AuditActorType.USER,
                    ActorUserIdOverride: user.Id), ct);
            }, cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            logger.LogWarning("Register failed: duplicate email (concurrent)");
            User? concurrentExisting = await userStore.GetByEmail(request.Email, cancellationToken);
            if (concurrentExisting is not null)
            {
                await RecordExistingAccountAttempt(concurrentExisting, cancellationToken);
            }
            return Result.Success();
        }
        catch (DuplicateUsernameException)
        {
            logger.LogWarning("Register failed: duplicate username (concurrent)");
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_REGISTER,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_USERNAME_ALREADY_EXISTS },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return Result.Conflict(ErrorCodes.ERR_USERNAME_ALREADY_EXISTS);
        }

        logger.LogInformation("Register succeeded: UserId={UserId}", user!.Id);
        return Result.Success();
    }

    private async Task RecordExistingAccountAttempt(User existing, CancellationToken cancellationToken)
    {
        logger.LogWarning("Registration attempt used an existing email");
        EmailDispatchPayload notice = emailComposer.CreateRegistrationAttemptNotice(existing.Email, existing.Id);
        await outboxStore.Enqueue(OutboxMessageTypes.EMAIL_DISPATCH, notice, cancellationToken);
        await auditWriter.WriteBestEffort(new AuditEvent(
            AuditActions.AUTH_REGISTER,
            AuditOutcome.FAILURE,
            AuditResourceTypes.USER,
            Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS },
            ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
    }
}
