using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Tags.DeleteTag;

internal sealed class DeleteTagCommandHandler(
    ITagStore tagStore,
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

        await tagStore.Delete(tag, cancellationToken);
        logger.LogInformation("Deleted tag: {TagId}", request.Id);
        
        await cache.RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, cancellationToken);
        await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);

        return Result.Success();
    }
}
