using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data;
using System.Text;
using System.Text.Json;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class ListingCopilotStore(ApplicationDbContext dbContext) : IListingCopilotStore
{
    private static readonly JsonSerializerOptions _tagsJson = new();

    public async Task<ListingCopilotOwnedVersion?> GetOwnedVersion(
        Guid assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.AssetVersions
            .AsNoTracking()
            .Where(v => v.Id == assetVersionId && v.Asset.AuthorId == ownerUserId)
            .Select(v => new ListingCopilotOwnedVersion(
                v.AssetId,
                v.Id,
                v.ProcessingStatus,
                v.ArchiveAnalysis != null,
                v.FileName))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListCategoryNames(CancellationToken cancellationToken = default)
    {
        return await dbContext.Categories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => c.Name)
            .Take(ListingSuggestionBounds.MAX_ALLOWLIST_CATEGORIES + 1)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListTagNames(CancellationToken cancellationToken = default)
    {
        return await dbContext.Tags
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => t.Name)
            .Take(ListingSuggestionBounds.MAX_ALLOWLIST_TAGS + 1)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryCommitSucceeded(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        ListingCopilotSuggestionWrite suggestion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (suggestion.JobId != jobId)
        {
            return false;
        }

        var tagsJson = JsonSerializer.Serialize(suggestion.Tags, _tagsJson);
        if (Encoding.UTF8.GetByteCount(tagsJson) > ListingSuggestionBounds.TAGS_JSON_MAX_BYTES
            || suggestion.Tags.Count > ListingSuggestionBounds.MAX_SUGGESTED_TAGS)
        {
            return false;
        }

        var resultJson = AssetProcessingSerializer.SerializeResult(
            AssetProcessingJobType.LISTING_COPILOT,
            new ListingCopilotResult(true, suggestion.ContentHash));

        await using var tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var locked = await dbContext.AssetProcessingJobs
                .FromSqlInterpolated($"""
                                      SELECT *
                                      FROM asset_processing_jobs
                                      WHERE "Id" = {jobId}
                                        AND "LeaseExpiresAt" > clock_timestamp()
                                      FOR UPDATE
                                      """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            if (locked.Count != 1)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            var job = locked[0];
            if (job.Id != jobId
                || job.Type != AssetProcessingJobType.LISTING_COPILOT
                || job.AssetId != assetId
                || job.AssetVersionId != assetVersionId
                || job.Status != AssetProcessingJobStatus.RUNNING
                || job.LeaseToken != leaseToken)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                 INSERT INTO asset_listing_suggestions (
                     "JobId",
                     "PromptPolicyVersion",
                     "Provider",
                     "ModelId",
                     "ModelRevision",
                     "UpstreamProvider",
                     "ProviderRequestId",
                     "Title",
                     "Description",
                     "Category",
                     "Tags",
                     "ContentHash",
                     "InputTokens",
                     "OutputTokens",
                     "CreatedAt")
                 VALUES (
                     {jobId},
                     {suggestion.PromptPolicyVersion},
                     {suggestion.Provider.ToString()},
                     {suggestion.ModelId},
                     {Truncate(suggestion.ModelRevision, ListingSuggestionBounds.MODEL_REVISION_MAX_LENGTH)},
                     {Truncate(suggestion.UpstreamProvider, ListingSuggestionBounds.UPSTREAM_PROVIDER_MAX_LENGTH)},
                     {Truncate(suggestion.ProviderRequestId, ListingSuggestionBounds.REQUEST_ID_MAX_LENGTH)},
                     {suggestion.Title},
                     {suggestion.Description},
                     {suggestion.Category},
                     {tagsJson}::jsonb,
                     {suggestion.ContentHash},
                     {suggestion.InputTokens},
                     {suggestion.OutputTokens},
                     clock_timestamp())
                 """, cancellationToken);

            var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                                                                                UPDATE asset_processing_jobs
                                                                                SET "Status" = 'SUCCEEDED',
                                                                                    "Stage" = 'SUCCEEDED',
                                                                                    "CompletedAt" = clock_timestamp(),
                                                                                    "Result" = {resultJson}::jsonb,
                                                                                    "ErrorCode" = NULL,
                                                                                    "ErrorSummary" = NULL,
                                                                                    "LeaseOwner" = NULL,
                                                                                    "LeaseToken" = NULL,
                                                                                    "LeaseExpiresAt" = NULL,
                                                                                    "UpdatedAt" = clock_timestamp()
                                                                                WHERE "Id" = {jobId}
                                                                                  AND "Type" = 'LISTING_COPILOT'
                                                                                  AND "AssetId" = {assetId}
                                                                                  AND "AssetVersionId" = {assetVersionId}
                                                                                  AND "Status" = 'RUNNING'
                                                                                  AND "LeaseToken" = {leaseToken}
                                                                                  AND "LeaseExpiresAt" > clock_timestamp()
                                                                                """, cancellationToken);

            if (inserted != 1 || updated != 1)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            await tx.CommitAsync(cancellationToken);
            return true;
        }
        catch (PostgresException ex) when (
            ex is { SqlState: "23505", ConstraintName: AssetListingSuggestionConfiguration.PRIMARY_KEY })
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }
    }

    public async Task<ListingCopilotSuggestionDto?> GetSuggestionForOwner(
        Guid assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var owned = await dbContext.AssetVersions
            .AsNoTracking()
            .AnyAsync(v => v.Id == assetVersionId && v.Asset.AuthorId == ownerUserId, cancellationToken);
        if (!owned)
        {
            return null;
        }

        var row = await dbContext.AssetListingSuggestions
            .AsNoTracking()
            .Where(s => s.Job.AssetVersionId == assetVersionId
                && s.Job.Asset.AuthorId == ownerUserId
                && s.Job.Type == AssetProcessingJobType.LISTING_COPILOT)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new
            {
                s.JobId,
                s.Job.AssetVersionId,
                s.Title,
                s.Description,
                s.Category,
                s.Tags,
                s.Provider,
                s.ModelId,
                s.ModelRevision,
                s.UpstreamProvider,
                s.CreatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var tags = JsonSerializer.Deserialize<List<string>>(row.Tags, _tagsJson) ?? [];
        return new ListingCopilotSuggestionDto(
            row.JobId,
            row.AssetVersionId,
            row.Title,
            row.Description,
            row.Category,
            tags,
            row.Provider,
            row.ModelId,
            row.ModelRevision,
            row.UpstreamProvider,
            row.CreatedAt);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
