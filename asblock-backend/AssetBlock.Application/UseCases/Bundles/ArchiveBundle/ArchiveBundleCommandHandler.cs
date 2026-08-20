using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Bundles.ArchiveBundle;

internal sealed class ArchiveBundleCommandHandler(
    IBundleStore bundleStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<ArchiveBundleCommandHandler> logger)
    : IRequestHandler<ArchiveBundleCommand, Result>
{
    public async Task<Result> Handle(ArchiveBundleCommand request, CancellationToken cancellationToken)
    {
        var bundle = await bundleStore.GetById(request.BundleId, cancellationToken);
        if (bundle is null)
        {
            return Result.NotFound(ErrorCodes.ERR_BUNDLE_NOT_FOUND);
        }

        if (bundle.SellerId != request.SellerId)
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.BUNDLE_ARCHIVE,
                AuditOutcome.DENIED,
                AuditResourceTypes.BUNDLE,
                request.BundleId.ToString()), cancellationToken);
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        if (bundle.ArchivedAt.HasValue)
        {
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
        }

        var archived = false;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            archived = await bundleStore.TryArchive(request.BundleId, request.SellerId, DateTimeOffset.UtcNow, ct);
            if (archived)
            {
                await auditWriter.Write(new AuditEvent(
                    AuditActions.BUNDLE_ARCHIVE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.BUNDLE,
                    request.BundleId.ToString()), ct);
            }
        }, cancellationToken);

        if (!archived)
        {
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
        }

        logger.LogInformation("Archived bundle {BundleId} by seller {SellerId}", request.BundleId, request.SellerId);
        return Result.Success();
    }
}
