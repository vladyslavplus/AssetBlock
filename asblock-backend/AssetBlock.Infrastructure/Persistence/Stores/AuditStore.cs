using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class AuditStore(ApplicationDbContext dbContext) : IAuditStore
{
    private static readonly JsonDocumentOptions _jsonDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 32
    };

    public async Task Add(AuditLog entry, CancellationToken cancellationToken = default)
    {
        dbContext.AuditLogs.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<AuditLogListItem>> GetPaged(
        GetAuditLogsRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<AuditLog> query = dbContext.AuditLogs.AsNoTracking();

        if (request.ActorUserId is { } actorUserId)
        {
            query = query.Where(e => e.ActorUserId == actorUserId);
        }

        if (request.ActorType is { } actorType)
        {
            query = query.Where(e => e.ActorType == actorType);
        }

        if (!string.IsNullOrWhiteSpace(request.Action))
        {
            var action = request.Action.Trim();
            query = query.Where(e => e.Action == action);
        }

        if (request.Outcome is { } outcome)
        {
            query = query.Where(e => e.Outcome == outcome);
        }

        if (!string.IsNullOrWhiteSpace(request.ResourceType))
        {
            var resourceType = request.ResourceType.Trim();
            query = query.Where(e => e.ResourceType == resourceType);
        }

        if (!string.IsNullOrWhiteSpace(request.ResourceId))
        {
            var resourceId = request.ResourceId.Trim();
            query = query.Where(e => e.ResourceId == resourceId);
        }

        if (request.From is { } from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        if (request.To is { } to)
        {
            query = query.Where(e => e.OccurredAt <= to);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);

        var rows = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id,
                e.OccurredAt,
                e.ActorType,
                e.ActorUserId,
                e.Action,
                e.Outcome,
                e.ResourceType,
                e.ResourceId,
                e.TraceId,
                e.IpAddress,
                e.UserAgent,
                e.MetadataJson
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(e => new AuditLogListItem(
            e.Id,
            e.OccurredAt,
            e.ActorType,
            e.ActorUserId,
            e.Action,
            e.Outcome,
            e.ResourceType,
            e.ResourceId,
            e.TraceId,
            e.IpAddress,
            e.UserAgent,
            ParseMetadata(e.MetadataJson))).ToList();

        return new PagedResult<AuditLogListItem>(items, totalCount, page, pageSize);
    }

    private static JsonElement? ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(metadataJson, _jsonDocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return document.RootElement.Clone();
    }
}
