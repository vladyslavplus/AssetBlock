using System.Data;
using System.Data.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace AssetBlock.Infrastructure.Persistence.Stores;

public sealed class AssetEmbeddingFinalizer(
    ApplicationDbContext dbContext,
    ICacheService cache,
    ILogger<AssetEmbeddingFinalizer> logger) : IAssetEmbeddingFinalizer
{
    private const string JOB_TYPE_EMBEDDING = nameof(AssetProcessingJobType.EMBEDDING_GENERATION);
    private const string JOB_STATUS_SUCCEEDED = nameof(AssetProcessingJobStatus.SUCCEEDED);
    private const string JOB_STATUS_RUNNING = nameof(AssetProcessingJobStatus.RUNNING);
    private const string VERSION_STATUS_READY = nameof(AssetVersionProcessingStatus.READY);

    private sealed class JobRow
    {
        public Guid Id { get; set; }
        public Guid AssetId { get; set; }
        public Guid AssetVersionId { get; set; }
        public string Type { get; set; } = null!;
        public string Status { get; set; } = null!;
        public Guid? LeaseToken { get; set; }
        public DateTimeOffset? LeaseExpiresAt { get; set; }
        public string? InputHash { get; set; }
        public string? ModelKey { get; set; }
    }

    private sealed class AssetRow
    {
        public Guid Id { get; set; }
        public long SearchRevision { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public Guid? CategoryId { get; set; }
        public string Title { get; set; } = null!;
        public string Description { get; set; } = null!;
    }

    public async Task<EmbeddingFinalizationStatus> Finalize(
        FinalizeEmbeddingParameters parameters,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Lock and validate job row FIRST
        List<JobRow> jobs = await dbContext.Database.SqlQueryRaw<JobRow>(
            """
            SELECT "Id", "AssetId", "AssetVersionId", "Type", "Status", "LeaseToken", "LeaseExpiresAt", "InputHash", "ModelKey"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
            FOR UPDATE
            """, parameters.JobId).ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return EmbeddingFinalizationStatus.LEASE_LOST;
        }

        // Query clock_timestamp() strictly AFTER obtaining FOR UPDATE lock
        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        JobRow job = jobs[0];

        if (job is { Status: JOB_STATUS_SUCCEEDED, Type: JOB_TYPE_EMBEDDING }
            && job.AssetId == parameters.AssetId)
        {
            await tx.CommitAsync(cancellationToken);
            return EmbeddingFinalizationStatus.COMMITTED;
        }

        if (job.Status != JOB_STATUS_RUNNING
            || job.LeaseToken != parameters.LeaseToken
            || job.LeaseExpiresAt <= dbNow
            || job.Type != JOB_TYPE_EMBEDDING
            || job.AssetId != parameters.AssetId)
        {
            return EmbeddingFinalizationStatus.LEASE_LOST;
        }

        if (!string.Equals(job.InputHash?.Trim(), parameters.ContentHash.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(job.ModelKey?.Trim(), parameters.ModelKey.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            await MarkJobSucceededInternal(parameters.JobId, parameters, dbNow, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return EmbeddingFinalizationStatus.NO_OP_STALE_OR_SUPERCEDED;
        }

        // 2. Lock and validate asset row
        List<AssetRow> assets = await dbContext.Database.SqlQueryRaw<AssetRow>(
            """
            SELECT "Id", "SearchRevision", "DeletedAt", "CategoryId", "Title", "Description"
            FROM assets
            WHERE "Id" = {0}
            FOR UPDATE
            """, parameters.AssetId).ToListAsync(cancellationToken);

        if (assets.Count == 0 || assets[0].DeletedAt != null)
        {
            await MarkJobSucceededInternal(parameters.JobId, parameters, dbNow, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return EmbeddingFinalizationStatus.NO_OP_STALE_OR_SUPERCEDED;
        }

        AssetRow asset = assets[0];

        // If incoming SourceRevision is older than current asset SearchRevision, asset was updated
        if (asset.SearchRevision > parameters.SourceRevision)
        {
            await MarkJobSucceededInternal(parameters.JobId, parameters, dbNow, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return EmbeddingFinalizationStatus.NO_OP_STALE_OR_SUPERCEDED;
        }

        // 3. Verify current version is READY
        List<Guid> readyVersions = await dbContext.Database.SqlQueryRaw<Guid>(
            """
            SELECT "Id" AS "Value"
            FROM asset_versions
            WHERE "AssetId" = {0} AND "IsCurrent" = true AND "ProcessingStatus" = {1}
            """, parameters.AssetId, VERSION_STATUS_READY).ToListAsync(cancellationToken);

        if (readyVersions.Count == 0)
        {
            await MarkJobSucceededInternal(parameters.JobId, parameters, dbNow, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return EmbeddingFinalizationStatus.NO_OP_STALE_OR_SUPERCEDED;
        }

        // 4. Re-verify canonical metadata hash from database
        string? categoryName = null;
        if (asset.CategoryId.HasValue)
        {
            categoryName = await dbContext.Database.SqlQueryRaw<string>(
                """
                SELECT "Name" AS "Value"
                FROM categories
                WHERE "Id" = {0}
                """, asset.CategoryId.Value).FirstOrDefaultAsync(cancellationToken);
        }

        List<string> tagNames = await dbContext.Database.SqlQueryRaw<string>(
            """
            SELECT t."Name" AS "Value"
            FROM tags t
            JOIN asset_tags at ON at."TagId" = t."Id"
            WHERE at."AssetId" = {0}
            ORDER BY t."Name"
            """, parameters.AssetId).ToListAsync(cancellationToken);

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(
            asset.Title,
            asset.Description,
            categoryName,
            tagNames);

        if (!string.Equals(canonical.ContentHash, parameters.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            await MarkJobSucceededInternal(parameters.JobId, parameters, dbNow, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return EmbeddingFinalizationStatus.NO_OP_STALE_OR_SUPERCEDED;
        }

        // 5. Monotonic upsert into asset_embeddings
        var newEmbeddingId = Guid.NewGuid();
        var vector = new Vector(parameters.Vector);

        DbConnection connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using DbCommand cmd = connection.CreateCommand();
        cmd.Transaction = tx.GetDbTransaction();
        cmd.CommandText = """
            INSERT INTO asset_embeddings (
                "Id", "AssetId", "ModelKey", "Provider", "ModelId", "ModelRevision",
                "ModelDigest", "Dimension", "ContentSchemaVersion", "SourceRevision",
                "ContentHash", "Embedding", "CreatedAt", "UpdatedAt"
            )
            VALUES (
                @id, @assetId, @modelKey, @provider, @modelId, @modelRevision,
                @modelDigest, @dimension, @contentSchemaVersion, @sourceRevision,
                @contentHash, @embedding, clock_timestamp(), clock_timestamp()
            )
            ON CONFLICT ("AssetId", "ModelKey") DO UPDATE
            SET "SourceRevision" = EXCLUDED."SourceRevision",
                "ContentHash" = EXCLUDED."ContentHash",
                "Embedding" = EXCLUDED."Embedding",
                "UpdatedAt" = clock_timestamp()
            WHERE EXCLUDED."SourceRevision" >= asset_embeddings."SourceRevision";
            """;

        AddParam(cmd, "@id", newEmbeddingId);
        AddParam(cmd, "@assetId", parameters.AssetId);
        AddParam(cmd, "@modelKey", parameters.ModelKey);
        AddParam(cmd, "@provider", parameters.Provider);
        AddParam(cmd, "@modelId", parameters.ModelId);
        AddParam(cmd, "@modelRevision", parameters.ModelRevision);
        AddParam(cmd, "@modelDigest", parameters.ModelDigest);
        AddParam(cmd, "@dimension", parameters.Dimension);
        AddParam(cmd, "@contentSchemaVersion", parameters.ContentSchemaVersion);
        AddParam(cmd, "@sourceRevision", parameters.SourceRevision);
        AddParam(cmd, "@contentHash", parameters.ContentHash);
        AddParam(cmd, "@embedding", vector);

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // 6. Mark job as succeeded
        await MarkJobSucceededInternal(parameters.JobId, parameters, dbNow, cancellationToken);

        await tx.CommitAsync(cancellationToken);

        // 7. Invalidate catalog cache after commit
        try
        {
            await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to invalidate catalog cache after embedding finalization.");
        }

        return EmbeddingFinalizationStatus.COMMITTED;
    }

    public async Task<bool> MarkJobNoOp(
        Guid jobId,
        Guid leaseToken,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Lock the job row first to ensure any competing lock completes before checking lease expiry
        List<JobRow> jobs = await dbContext.Database.SqlQueryRaw<JobRow>(
            """
            SELECT "Id", "AssetId", "AssetVersionId", "Type", "Status", "LeaseToken", "LeaseExpiresAt", "InputHash", "ModelKey"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
            FOR UPDATE
            """, jobId).ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return false;
        }

        // Query clock_timestamp() strictly after acquiring the row lock
        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        JobRow job = jobs[0];
        if (job.Status != JOB_STATUS_RUNNING
            || job.LeaseToken != leaseToken
            || job.LeaseExpiresAt <= dbNow)
        {
            return false;
        }

        var rows = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = {JOB_STATUS_SUCCEEDED},
                "Stage" = {JOB_STATUS_SUCCEEDED},
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL
            WHERE "Id" = {jobId}
              AND "Status" = {JOB_STATUS_RUNNING}
              AND "LeaseToken" = {leaseToken}
            """, cancellationToken);

        if (rows > 0)
        {
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        await tx.RollbackAsync(cancellationToken);
        return false;
    }

    private async Task MarkJobSucceededInternal(
        Guid jobId,
        FinalizeEmbeddingParameters parameters,
        DateTimeOffset dbNow,
        CancellationToken cancellationToken)
    {
        var resultDto = new EmbeddingGenerationResult(
            parameters.AssetId,
            parameters.SourceRevision,
            parameters.ContentHash,
            parameters.ModelKey,
            parameters.Dimension,
            Success: true);
        var serializedResult = AssetProcessingSerializer.SerializeResult(
            AssetProcessingJobType.EMBEDDING_GENERATION,
            resultDto);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = {JOB_STATUS_SUCCEEDED},
                "Stage" = {JOB_STATUS_SUCCEEDED},
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow},
                "Result" = CAST({serializedResult} AS jsonb),
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL
            WHERE "Id" = {jobId}
            """, cancellationToken);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
