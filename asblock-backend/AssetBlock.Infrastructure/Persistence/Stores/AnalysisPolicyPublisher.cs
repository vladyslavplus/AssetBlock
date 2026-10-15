using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// Publishes the approved analysis policy and runtime manifest as one immutable row. The row is
/// content-addressed by (PolicyKey, PolicyDigest) and never edited: republishing an identical
/// manifest is a no-op, giving one key a second approved digest is refused, and changing the
/// content behind an existing pair is refused.
/// <para>
/// The check and the insert are serialized per policy key by a transaction-scoped advisory lock
/// derived from that key, so two publishers that race on the same new key cannot both decide the
/// key is unclaimed. The unique index stays the last line of defence and only its own violation is
/// translated.
/// </para>
/// </summary>
internal sealed class AnalysisPolicyPublisher(
    ApplicationDbContext dbContext,
    ILogger<AnalysisPolicyPublisher> logger) : IAnalysisPolicyPublisher
{
    private const string POLICY_IDENTITY_INDEX = "UIX_code_analysis_policy_versions_identity";

    public async Task<AnalysisPolicyPublishResult> Publish(
        AnalysisPolicyManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var rejection = AnalysisManifestContract.Validate(manifest);
        if (rejection is not null)
        {
            logger.LogError("Refusing to publish the analysis policy manifest: {Reason}", rejection);
            return new AnalysisPolicyPublishResult(AnalysisPolicyPublishOutcome.REJECTED, Guid.Empty, rejection);
        }

        // The lock only spans this read and insert; nothing outside publication happens under it.
        await using IDbContextTransaction transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await LockPolicyKey(manifest.PolicyKey, cancellationToken).ConfigureAwait(false);

        List<PublishedPolicyRow> existing = await ReadByKey(manifest.PolicyKey, cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            AnalysisPolicyPublishResult reconciled = Reconcile(manifest, existing);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return reconciled;
        }

        var policyVersion = new CodeAnalysisPolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyKey = manifest.PolicyKey,
            PolicyDigest = manifest.PolicyDigest,
            SchemaName = manifest.SchemaName,
            StageMatrixJson = manifest.StageMatrixJson,
            VerifierMode = manifest.VerifierMode,
            VerifierReason = manifest.VerifierReason,
            ToolIdentitiesJson = manifest.ToolIdentitiesJson,
            LimitsJson = manifest.LimitsJson,
            SecurityValidityJson = manifest.SecurityValidityJson,
            ModelKey = manifest.ModelKey,
            IndexKey = manifest.IndexKey,
            ReferenceSnapshotId = manifest.ReferenceSnapshotId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.CodeAnalysisPolicyVersions.Add(policyVersion);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsPolicyIdentityViolation(ex))
        {
            // Only the policy identity index is translated. Any other failure is unexpected and is
            // rethrown so it can never be mistaken for a benign concurrent publication.
            dbContext.Entry(policyVersion).State = EntityState.Detached;
            dbContext.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            logger.LogWarning(ex, "A concurrent publisher won the identity race for policy key {PolicyKey}.", manifest.PolicyKey);
            List<PublishedPolicyRow> concurrent = await ReadByKey(manifest.PolicyKey, cancellationToken).ConfigureAwait(false);
            return Reconcile(manifest, concurrent);
        }

        logger.LogInformation(
            "Published analysis policy {PolicyKey} digest {PolicyDigest} runtime manifest {RuntimeManifestDigest}.",
            manifest.PolicyKey, manifest.PolicyDigest, RuntimeManifestDigestOf(manifest));

        return new AnalysisPolicyPublishResult(
            AnalysisPolicyPublishOutcome.PUBLISHED, policyVersion.Id, "The approved manifest was published.");
    }

    /// <summary>
    /// Serializes publication for one policy key across processes. The lock key is derived from the
    /// policy key itself, so no additional row or table is needed to protect the unclaimed-key case
    /// that a row lock cannot cover.
    /// </summary>
    private async Task LockPolicyKey(string policyKey, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT pg_advisory_xact_lock(('x' || substr(md5({policyKey}), 1, 16))::bit(64)::bigint)
            """, cancellationToken).ConfigureAwait(false);
    }

    private Task<List<PublishedPolicyRow>> ReadByKey(string policyKey, CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<PublishedPolicyRow>(
            """
            SELECT "Id", "PolicyDigest", "SchemaName", "StageMatrixJson"::text AS "StageMatrixJson",
                   "ToolIdentitiesJson"::text AS "ToolIdentitiesJson", "VerifierMode", "VerifierReason",
                   "LimitsJson"::text AS "LimitsJson", "SecurityValidityJson"::text AS "SecurityValidityJson",
                   "ModelKey", "IndexKey", "ReferenceSnapshotId"
            FROM code_analysis_policy_versions
            WHERE "PolicyKey" = {0}
            """, policyKey).ToListAsync(cancellationToken);

    /// <summary>
    /// Decides an already-present key: an identical manifest is an idempotent success, while a
    /// second approved digest under the same key, or changed content behind an existing pair, is an
    /// immutable-identity conflict that publication refuses.
    /// </summary>
    private AnalysisPolicyPublishResult Reconcile(AnalysisPolicyManifest manifest, List<PublishedPolicyRow> existing)
    {
        if (existing.Count == 0)
        {
            var raced =
                $"Policy key '{manifest.PolicyKey}' was not persisted by the concurrent publisher that won " +
                "the identity race; publication must be retried.";

            logger.LogError("Refusing to publish the analysis policy manifest: {Conflict}", raced);
            return new AnalysisPolicyPublishResult(AnalysisPolicyPublishOutcome.CONFLICT, Guid.Empty, raced);
        }

        PublishedPolicyRow? sameKey = existing.Find(r => r.PolicyDigest == manifest.PolicyDigest);

        if (sameKey is null)
        {
            var conflict =
                $"Policy key '{manifest.PolicyKey}' already approves digest '{existing[0].PolicyDigest}'; " +
                $"digest '{manifest.PolicyDigest}' may not be published under the same key.";

            logger.LogError("Refusing to publish the analysis policy manifest: {Conflict}", conflict);
            return new AnalysisPolicyPublishResult(AnalysisPolicyPublishOutcome.CONFLICT, Guid.Empty, conflict);
        }

        if (!IsIdenticalContent(sameKey, manifest))
        {
            var conflict =
                $"Policy '{manifest.PolicyKey}' digest '{manifest.PolicyDigest}' is already published with " +
                "different content and may not be edited in place.";

            logger.LogError("Refusing to publish the analysis policy manifest: {Conflict}", conflict);
            return new AnalysisPolicyPublishResult(AnalysisPolicyPublishOutcome.CONFLICT, sameKey.Id, conflict);
        }

        logger.LogInformation(
            "Analysis policy {PolicyKey} digest {PolicyDigest} is already published unchanged.",
            manifest.PolicyKey, manifest.PolicyDigest);

        return new AnalysisPolicyPublishResult(
            AnalysisPolicyPublishOutcome.PUBLISHED, sameKey.Id, "The approved manifest is already published.");
    }

    /// <summary>
    /// Compares the stored row with the declared manifest through the canonical runtime-manifest
    /// digest. JSON members are compared canonically because jsonb normalization means byte-identical
    /// text is not what was persisted.
    /// </summary>
    private static bool IsIdenticalContent(PublishedPolicyRow row, AnalysisPolicyManifest manifest) =>
        string.Equals(RuntimeManifestDigestOf(row), RuntimeManifestDigestOf(manifest), StringComparison.Ordinal);

    private static string RuntimeManifestDigestOf(PublishedPolicyRow row) =>
        ProductDigests.BuildRuntimeManifestDigest(
            row.SchemaName,
            row.StageMatrixJson,
            row.ToolIdentitiesJson,
            row.VerifierMode,
            row.VerifierReason,
            row.LimitsJson,
            row.SecurityValidityJson,
            row.ModelKey,
            row.IndexKey,
            row.ReferenceSnapshotId);

    private static string RuntimeManifestDigestOf(AnalysisPolicyManifest manifest) =>
        ProductDigests.BuildRuntimeManifestDigest(
            manifest.SchemaName,
            manifest.StageMatrixJson,
            manifest.ToolIdentitiesJson,
            manifest.VerifierMode,
            manifest.VerifierReason,
            manifest.LimitsJson,
            manifest.SecurityValidityJson,
            manifest.ModelKey,
            manifest.IndexKey,
            manifest.ReferenceSnapshotId);

    private static bool IsPolicyIdentityViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: POLICY_IDENTITY_INDEX
        };

    private sealed class PublishedPolicyRow
    {
        public Guid Id { get; init; }
        public string PolicyDigest { get; init; } = "";
        public string SchemaName { get; init; } = "";
        public string StageMatrixJson { get; init; } = "";
        public string ToolIdentitiesJson { get; init; } = "";
        public string VerifierMode { get; init; } = "";
        public string VerifierReason { get; init; } = "";
        public string LimitsJson { get; init; } = "";
        public string SecurityValidityJson { get; init; } = "";
        public string? ModelKey { get; init; }
        public string? IndexKey { get; init; }
        public string? ReferenceSnapshotId { get; init; }
    }
}
