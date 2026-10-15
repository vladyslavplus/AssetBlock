using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// Fail-closed readiness for shared analysis claims: stable capacity registration, the exact
/// persisted pinned policy row (key, digest and report contract), the complete pinned runtime
/// manifest that row authorizes, and the registered handler must all be proven before any new
/// CODE_ANALYSIS claim. A blank or mismatched configuration never burns queued attempts; it only
/// closes new claims while recovery of occupied reservations continues elsewhere.
/// <para>
/// Readiness proves approved configuration only: that the persisted row is the manifest this
/// deployment pinned. It does not claim the running service actually loaded that manifest; that is
/// proven per run when the admitted report's observed stage, tool and reference identity is
/// validated against this same manifest.
/// </para>
/// </summary>
internal sealed class AnalysisReadinessGate(
    ApplicationDbContext dbContext,
    IAnalysisCapacityStore capacityStore,
    ICodeAnalysisReportFinalizer finalizer,
    IAssetProcessingJobRegistry registry,
    IOptions<CodeAnalysisOptions> analysisOptions,
    ILogger<AnalysisReadinessGate> logger) : IAnalysisReadinessGate
{
    public async Task<bool> EnsureReady(CancellationToken cancellationToken = default)
    {
        CodeAnalysisOptions options = analysisOptions.Value;
        if (string.IsNullOrWhiteSpace(options.PolicyKey)
            || string.IsNullOrWhiteSpace(options.PolicyDigest))
        {
            logger.LogError("Pinned code analysis policy is not configured; analysis claims stay closed.");
            return false;
        }

        // The pinned runtime manifest is part of the approval, not an optional extra: without it a
        // row could share the key and policy digest while authorizing different stages, tools,
        // limits or security validity than this deployment approved.
        if (string.IsNullOrWhiteSpace(options.RuntimeManifestDigest))
        {
            logger.LogError("Pinned code analysis runtime manifest is not configured; analysis claims stay closed.");
            return false;
        }

        try
        {
            await capacityStore.EnsureSlots(options.ServiceKey, options.Capacity, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Analysis readiness for service {ServiceKey} is unavailable; analysis claims stay closed.", options.ServiceKey);
            return false;
        }

        // The pinned policy must exist exactly as configured and speak the admitted report
        // contract. Nonblank options alone never authorize a claim.
        CodeAnalysisPolicyVersion? pinned = await dbContext.CodeAnalysisPolicyVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.PolicyKey == options.PolicyKey
                    && p.PolicyDigest == options.PolicyDigest
                    && p.SchemaName == ProductDigests.ANALYSIS_SCHEMA,
                cancellationToken);
        if (pinned is null)
        {
            logger.LogError(
                "Pinned code analysis policy {PolicyKey} is not persisted exactly as configured; analysis claims stay closed.",
                options.PolicyKey);
            return false;
        }

        var persistedManifestDigest = ProductDigests.BuildRuntimeManifestDigest(
            pinned.SchemaName,
            pinned.StageMatrixJson,
            pinned.ToolIdentitiesJson,
            pinned.VerifierMode,
            pinned.VerifierReason,
            pinned.LimitsJson,
            pinned.SecurityValidityJson,
            pinned.ModelKey,
            pinned.IndexKey,
            pinned.ReferenceSnapshotId);

        if (!string.Equals(persistedManifestDigest, options.RuntimeManifestDigest, StringComparison.Ordinal))
        {
            logger.LogError(
                "Persisted code analysis runtime manifest {PersistedDigest} is not the pinned manifest {PinnedDigest}; analysis claims stay closed.",
                persistedManifestDigest,
                options.RuntimeManifestDigest);
            return false;
        }

        // The finalizer is a constructor dependency so a missing registration fails fast at
        // resolution time instead of burning attempts through queued claims.
        _ = finalizer;
        if (registry.GetHandler(AssetProcessingJobType.CODE_ANALYSIS) is null)
        {
            logger.LogError("No CODE_ANALYSIS handler is registered; analysis claims stay closed.");
            return false;
        }

        return true;
    }
}
