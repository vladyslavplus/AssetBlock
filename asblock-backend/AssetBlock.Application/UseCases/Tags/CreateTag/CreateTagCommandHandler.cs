using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Tags.CreateTag;

internal sealed class CreateTagCommandHandler(
    ITagStore tagStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<CreateTagCommandHandler> logger) : IRequestHandler<CreateTagCommand, Result<TagDto>>
{
    public async Task<Result<TagDto>> Handle(CreateTagCommand request, CancellationToken cancellationToken)
    {
        var normalizedName = request.Name.Trim().ToLowerInvariant();

        var existing = await tagStore.GetByName(normalizedName, cancellationToken);
        if (existing is not null)
        {
            return Result.Conflict(ErrorCodes.ERR_TAG_ALREADY_EXISTS);
        }

        var tag = new Tag
        {
            Id = Guid.NewGuid(),
            Name = normalizedName
        };

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                await tagStore.Add(tag, ct);
                await auditWriter.Write(new AuditEvent(
                    AuditActions.TAG_CREATE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.TAG,
                    tag.Id.ToString()), ct);
            }, cancellationToken);
        }
        catch (DuplicateTagNameException)
        {
            logger.LogWarning("Create tag failed: duplicate name (concurrent) {TagName}", normalizedName);
            return Result.Conflict(ErrorCodes.ERR_TAG_ALREADY_EXISTS);
        }

        logger.LogInformation("Added new tag: {TagName}", normalizedName);
        await cache.RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, cancellationToken);

        return Result.Success(new TagDto(tag.Id, tag.Name));
    }
}
