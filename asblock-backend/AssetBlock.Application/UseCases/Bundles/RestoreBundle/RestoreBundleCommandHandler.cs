using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Bundles.RestoreBundle;

internal sealed class RestoreBundleCommandHandler(
    IBundleStore bundleStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<RestoreBundleCommandHandler> logger)
    : IRequestHandler<RestoreBundleCommand, Result>
{
    public async Task<Result> Handle(RestoreBundleCommand request, CancellationToken cancellationToken)
    {
        var bundle = await bundleStore.GetById(request.BundleId, cancellationToken);
        if (bundle is null)
        {
            return Result.NotFound(ErrorCodes.ERR_BUNDLE_NOT_FOUND);
        }

        if (bundle.SellerId != request.SellerId)
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.BUNDLE_RESTORE,
                AuditOutcome.DENIED,
                AuditResourceTypes.BUNDLE,
                request.BundleId.ToString()), cancellationToken);
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        if (!bundle.ArchivedAt.HasValue)
        {
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
        }

        var restored = false;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            restored = await bundleStore.TryRestore(request.BundleId, request.SellerId, DateTimeOffset.UtcNow, ct);
            if (restored)
            {
                await auditWriter.Write(new AuditEvent(
                    AuditActions.BUNDLE_RESTORE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.BUNDLE,
                    request.BundleId.ToString()), ct);
            }
        }, cancellationToken);

        if (!restored)
        {
            // Store re-validates current revision assets; false usually means unavailable items or race.
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
        }

        logger.LogInformation("Restored bundle {BundleId} by seller {SellerId}", request.BundleId, request.SellerId);
        return Result.Success();
    }
}
