using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Bundles.RestoreBundle;

internal sealed class RestoreBundleCommandHandler(
    IBundleStore bundleStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<RestoreBundleCommandHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<RestoreBundleCommand, Result>
{
    public Task<Result> Handle(RestoreBundleCommand request, CancellationToken cancellationToken)
    {
        return BundleLifecycleTransition.Execute(
            bundleStore,
            unitOfWork,
            auditWriter,
            logger,
            request.BundleId,
            request.SellerId,
            AuditActions.BUNDLE_RESTORE,
            isArchive: false,
            bundleStore.TryRestore,
            "Restored",
            cancellationToken,
            timeProvider);
    }
}
