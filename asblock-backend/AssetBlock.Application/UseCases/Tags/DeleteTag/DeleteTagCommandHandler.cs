using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Tags.DeleteTag;

internal sealed class DeleteTagCommandHandler(
    ITagStore tagStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<DeleteTagCommandHandler> logger) : IRequestHandler<DeleteTagCommand, Result>
{
    public async Task<Result> Handle(DeleteTagCommand request, CancellationToken cancellationToken)
    {
        var tag = await tagStore.GetById(request.Id, cancellationToken);
        if (tag is null)
        {
            return Result.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND);
        }

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            await tagStore.Delete(tag, ct);
            await auditWriter.Write(new AuditEvent(
                AuditActions.TAG_DELETE,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.TAG,
                request.Id.ToString()), ct);
        }, cancellationToken);

        logger.LogInformation("Deleted tag: {TagId}", request.Id);

        await cache.RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, cancellationToken);
        await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);

        return Result.Success();
    }
}
