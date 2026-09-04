using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Bundles.ArchiveBundle;

internal sealed class ArchiveBundleCommandHandler(
    IBundleStore bundleStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<ArchiveBundleCommandHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<ArchiveBundleCommand, Result>
{
    public Task<Result> Handle(ArchiveBundleCommand request, CancellationToken cancellationToken)
    {
        return BundleLifecycleTransition.Execute(
            bundleStore,
            unitOfWork,
            auditWriter,
            logger,
            request.BundleId,
            request.SellerId,
            AuditActions.BUNDLE_ARCHIVE,
            isArchive: true,
            bundleStore.TryArchive,
            "Archived",
            cancellationToken,
            timeProvider);
    }
}
