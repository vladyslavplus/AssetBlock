using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Bundles;

/// <summary>
/// Shared lifecycle transition runner for bundle archive and restore operations.
/// </summary>
internal static class BundleLifecycleTransition
{
    public static async Task<Result> Execute(
        IBundleStore bundleStore,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        ILogger logger,
        Guid bundleId,
        Guid sellerId,
        string auditAction,
        bool isArchive,
        Func<Guid, Guid, DateTimeOffset, CancellationToken, Task<bool>> transitionOp,
        string logActionVerb,
        CancellationToken cancellationToken)
    {
        var bundle = await bundleStore.GetById(bundleId, cancellationToken);
        if (bundle is null)
        {
            return Result.NotFound(ErrorCodes.ERR_BUNDLE_NOT_FOUND);
        }

        if (bundle.SellerId != sellerId)
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                auditAction,
                AuditOutcome.DENIED,
                AuditResourceTypes.BUNDLE,
                bundleId.ToString()), cancellationToken);
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        // State precondition: archive requires unarchived bundle; restore requires archived bundle.
        if (isArchive ? bundle.ArchivedAt.HasValue : !bundle.ArchivedAt.HasValue)
        {
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
        }

        var transitionSucceeded = false;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            transitionSucceeded = await transitionOp(bundleId, sellerId, DateTimeOffset.UtcNow, ct);
            if (transitionSucceeded)
            {
                await auditWriter.Write(new AuditEvent(
                    auditAction,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.BUNDLE,
                    bundleId.ToString()), ct);
            }
        }, cancellationToken);

        if (!transitionSucceeded)
        {
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
        }

        logger.LogInformation("{Action} bundle {BundleId} by seller {SellerId}", logActionVerb, bundleId, sellerId);
        return Result.Success();
    }
}
