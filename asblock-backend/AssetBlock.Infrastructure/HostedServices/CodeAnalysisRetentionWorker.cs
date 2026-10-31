using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.HostedServices;

/// <summary>Removes private analysis detail and extracted archive payloads after their 12-month clocks.</summary>
internal sealed class CodeAnalysisRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<CodeAnalysisRetentionWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(24);
    private const int BATCH_SIZE = 250;
    private const int MAX_BATCHES_PER_CYCLE = 20;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("IntegrationTesting"))
        {
            logger.LogInformation("CodeAnalysisRetentionWorker skipped in IntegrationTesting.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanup(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CodeAnalysisRetentionWorker cycle failed");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task<int> RunCleanup(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        DateTimeOffset cutoff = _timeProvider.GetUtcNow().AddMonths(-12);
        var removed = 0;

        // README and manifest metadata are private extracted archive content. Their authoritative
        // clock is archive processing time, so rows without a finalized report are covered too.
        for (var batch = 0; batch < MAX_BATCHES_PER_CYCLE; batch++)
        {
            var redacted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH eligible AS (
                    SELECT aa."AssetVersionId" AS "Id"
                    FROM asset_archive_analyses aa
                    JOIN asset_versions v ON v."Id" = aa."AssetVersionId"
                    JOIN assets a ON a."Id" = v."AssetId"
                    WHERE aa."CreatedAt" <= {cutoff}
                      AND (aa."ReadmeContent" IS NOT NULL OR aa."ManifestMetadata" IS NOT NULL)
                      AND NOT EXISTS (SELECT 1 FROM purchases p WHERE p."AssetId" = a."Id")
                      AND NOT EXISTS (SELECT 1 FROM publication_snapshots ps WHERE ps."AssetVersionId" = v."Id")
                      AND NOT EXISTS (SELECT 1 FROM checkout_reservations cr WHERE cr."AssetId" = a."Id")
                      AND NOT EXISTS (
                          SELECT 1 FROM checkout_intent_items ci
                          JOIN checkout_intents c ON c."Id" = ci."CheckoutIntentId"
                          WHERE ci."AssetId" = a."Id" AND c."Status" = 'PENDING')
                      AND NOT EXISTS (
                          SELECT 1 FROM checkout_intent_items ci
                          JOIN checkout_intents c ON c."Id" = ci."CheckoutIntentId"
                          WHERE ci."AssetId" = a."Id" AND c."StripeSessionId" IS NOT NULL
                            AND NOT EXISTS (SELECT 1 FROM orders o WHERE o."CheckoutIntentId" = c."Id"))
                      AND NOT EXISTS (
                          SELECT 1 FROM moderation_submissions ms
                          WHERE ms."AssetVersionId" = v."Id"
                            AND ms."State" IN ('DRAFT', 'SUBMITTED', 'IN_REVIEW', 'CHANGES_REQUESTED', 'SUPERSEDED', 'REJECTED'))
                      AND NOT EXISTS (
                          SELECT 1 FROM paid_checkout_reconciliation_holds ph
                          JOIN checkout_intents c ON c."Id" = ph."CheckoutIntentId"
                          JOIN checkout_intent_items ci ON ci."CheckoutIntentId" = c."Id"
                          WHERE ci."AssetId" = a."Id" AND ph."State" = 'HELD')
                      AND NOT EXISTS (
                          SELECT 1 FROM code_analysis_runs r
                          WHERE r."AssetVersionId" = v."Id" AND r."State" IN ('QUEUED', 'RUNNING'))
                      AND NOT EXISTS (
                          SELECT 1 FROM security_rechecks sr
                          WHERE sr."AssetVersionId" = v."Id" AND sr."State" IN ('QUEUED', 'INSPECTING', 'SCANNING'))
                      AND NOT EXISTS (
                          SELECT 1 FROM asset_processing_jobs j
                          WHERE j."AssetVersionId" = v."Id" AND j."Status" IN ('QUEUED', 'RUNNING', 'RETRY_SCHEDULED'))
                    ORDER BY aa."CreatedAt", aa."AssetVersionId"
                    LIMIT {BATCH_SIZE}
                    FOR UPDATE OF a SKIP LOCKED
                )
                UPDATE asset_archive_analyses aa
                SET "ReadmeContent" = NULL,
                    "ManifestMetadata" = NULL,
                    "UpdatedAt" = clock_timestamp()
                FROM eligible e
                WHERE aa."AssetVersionId" = e."Id"
                """, cancellationToken).ConfigureAwait(false);

            removed += redacted;
            if (redacted < BATCH_SIZE)
            {
                break;
            }
        }

        for (var batch = 0; batch < MAX_BATCHES_PER_CYCLE; batch++)
        {
            var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH eligible AS (
                    SELECT h."Id"
                    FROM code_analysis_report_headers h
                    JOIN assets a ON a."Id" = h."AssetId"
                    WHERE EXISTS (SELECT 1 FROM code_analysis_report_findings f WHERE f."ReportId" = h."Id")
                      AND NOT EXISTS (SELECT 1 FROM seller_responses r WHERE r."ReportId" = h."Id")
                      AND NOT EXISTS (SELECT 1 FROM moderation_finding_dispositions d WHERE d."ReportId" = h."Id")
                      AND h."IsFinalized" = TRUE
                      AND h."FinalizedAt" IS NOT NULL
                      AND h."FinalizedAt" <= {cutoff}
                      AND NOT EXISTS (SELECT 1 FROM purchases p WHERE p."AssetId" = h."AssetId")
                      AND NOT EXISTS (SELECT 1 FROM publication_snapshots ps WHERE ps."AssetVersionId" = h."AssetVersionId")
                      AND NOT EXISTS (SELECT 1 FROM checkout_reservations cr WHERE cr."AssetId" = h."AssetId")
                      AND NOT EXISTS (
                          SELECT 1 FROM checkout_intent_items ci
                          JOIN checkout_intents c ON c."Id" = ci."CheckoutIntentId"
                          WHERE ci."AssetId" = h."AssetId" AND c."Status" = 'PENDING')
                      AND NOT EXISTS (
                          SELECT 1 FROM checkout_intent_items ci
                          JOIN checkout_intents c ON c."Id" = ci."CheckoutIntentId"
                          WHERE ci."AssetId" = h."AssetId" AND c."StripeSessionId" IS NOT NULL
                            AND NOT EXISTS (SELECT 1 FROM orders o WHERE o."CheckoutIntentId" = c."Id"))
                      AND NOT EXISTS (
                          SELECT 1 FROM moderation_submissions ms
                          WHERE ms."AssetVersionId" = h."AssetVersionId"
                            AND (ms."CodeAnalysisReportHeaderId" = h."Id" OR EXISTS (
                                SELECT 1 FROM asset_versions v WHERE v."Id" = ms."AssetVersionId"
                                  AND v."SelectedCodeAnalysisReportId" = h."Id"))
                            AND ms."State" IN ('DRAFT', 'SUBMITTED', 'IN_REVIEW', 'CHANGES_REQUESTED', 'SUPERSEDED'))
                      AND NOT EXISTS (
                          SELECT 1 FROM moderation_submissions ms
                          WHERE ms."AssetVersionId" = h."AssetVersionId" AND ms."State" = 'REJECTED')
                      AND NOT EXISTS (
                          SELECT 1 FROM paid_checkout_reconciliation_holds ph
                          JOIN checkout_intents c ON c."Id" = ph."CheckoutIntentId"
                          JOIN checkout_intent_items ci ON ci."CheckoutIntentId" = c."Id"
                          WHERE ci."AssetId" = h."AssetId" AND ph."State" = 'HELD')
                      AND NOT EXISTS (
                          SELECT 1 FROM code_analysis_runs r
                          WHERE r."AssetVersionId" = h."AssetVersionId" AND r."State" IN ('QUEUED', 'RUNNING'))
                      AND NOT EXISTS (
                          SELECT 1 FROM security_rechecks sr
                          WHERE sr."AssetVersionId" = h."AssetVersionId" AND sr."State" IN ('QUEUED', 'INSPECTING', 'SCANNING'))
                    ORDER BY a."Id", h."FinalizedAt", h."Id"
                    LIMIT {BATCH_SIZE}
                    FOR UPDATE OF a SKIP LOCKED
                )
                DELETE FROM code_analysis_report_findings f
                USING eligible e
                WHERE f."ReportId" = e."Id"
                """, cancellationToken).ConfigureAwait(false);

            removed += deleted;
            if (deleted < BATCH_SIZE)
            {
                break;
            }
        }

        for (var batch = 0; batch < MAX_BATCHES_PER_CYCLE; batch++)
        {
            var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH eligible AS (
                    SELECT al."Id"
                    FROM audit_logs al
                    JOIN code_analysis_report_headers h
                      ON al."ResourceType" = 'CodeAnalysisReport'
                     AND al."ResourceId" = h."Id"::text
                     AND al."Action" = 'CODE_ANALYSIS_FINALIZED'
                    JOIN assets a ON a."Id" = h."AssetId"
                    WHERE h."IsFinalized" = TRUE AND h."FinalizedAt" IS NOT NULL AND h."FinalizedAt" <= {cutoff}
                      AND NOT EXISTS (SELECT 1 FROM purchases p WHERE p."AssetId" = h."AssetId")
                      AND NOT EXISTS (SELECT 1 FROM publication_snapshots ps WHERE ps."AssetVersionId" = h."AssetVersionId")
                      AND NOT EXISTS (SELECT 1 FROM checkout_reservations cr WHERE cr."AssetId" = h."AssetId")
                      AND NOT EXISTS (
                          SELECT 1 FROM checkout_intent_items ci
                          JOIN checkout_intents c ON c."Id" = ci."CheckoutIntentId"
                          WHERE ci."AssetId" = h."AssetId" AND c."Status" = 'PENDING')
                      AND NOT EXISTS (
                          SELECT 1 FROM checkout_intent_items ci
                          JOIN checkout_intents c ON c."Id" = ci."CheckoutIntentId"
                          WHERE ci."AssetId" = h."AssetId" AND c."StripeSessionId" IS NOT NULL
                            AND NOT EXISTS (SELECT 1 FROM orders o WHERE o."CheckoutIntentId" = c."Id"))
                      AND NOT EXISTS (
                          SELECT 1 FROM moderation_submissions ms
                          WHERE ms."AssetVersionId" = h."AssetVersionId"
                            AND ms."State" IN ('DRAFT', 'SUBMITTED', 'IN_REVIEW', 'CHANGES_REQUESTED', 'SUPERSEDED'))
                      AND NOT EXISTS (
                          SELECT 1 FROM moderation_submissions ms
                          WHERE ms."AssetVersionId" = h."AssetVersionId" AND ms."State" = 'REJECTED')
                      AND NOT EXISTS (
                          SELECT 1 FROM paid_checkout_reconciliation_holds ph
                          JOIN checkout_intents c ON c."Id" = ph."CheckoutIntentId"
                          JOIN checkout_intent_items ci ON ci."CheckoutIntentId" = c."Id"
                          WHERE ci."AssetId" = h."AssetId" AND ph."State" = 'HELD')
                      AND NOT EXISTS (
                          SELECT 1 FROM code_analysis_runs r
                          WHERE r."AssetVersionId" = h."AssetVersionId" AND r."State" IN ('QUEUED', 'RUNNING'))
                      AND NOT EXISTS (
                          SELECT 1 FROM security_rechecks sr
                          WHERE sr."AssetVersionId" = h."AssetVersionId" AND sr."State" IN ('QUEUED', 'INSPECTING', 'SCANNING'))
                    ORDER BY a."Id", h."FinalizedAt", al."Id"
                    LIMIT {BATCH_SIZE}
                    FOR UPDATE OF a SKIP LOCKED
                )
                DELETE FROM audit_logs al
                USING eligible e
                WHERE al."Id" = e."Id"
                """, cancellationToken).ConfigureAwait(false);

            removed += deleted;
            if (deleted < BATCH_SIZE)
            {
                break;
            }
        }

        for (var batch = 0; batch < MAX_BATCHES_PER_CYCLE; batch++)
        {
            var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH eligible AS (
                    SELECT ra."Id"
                    FROM code_analysis_reuse_artifacts ra
                    JOIN code_analysis_report_headers h ON h."Id" = ra."SourceReportId"
                    JOIN assets a ON a."Id" = h."AssetId"
                    WHERE h."IsFinalized" = TRUE AND h."FinalizedAt" IS NOT NULL AND h."FinalizedAt" <= {cutoff}
                      AND NOT EXISTS (SELECT 1 FROM purchases p WHERE p."AssetId" = h."AssetId")
                      AND NOT EXISTS (SELECT 1 FROM publication_snapshots ps WHERE ps."AssetVersionId" = h."AssetVersionId")
                      AND NOT EXISTS (SELECT 1 FROM checkout_reservations cr WHERE cr."AssetId" = h."AssetId")
                      AND NOT EXISTS (
                          SELECT 1 FROM checkout_intent_items ci
                          JOIN checkout_intents c ON c."Id" = ci."CheckoutIntentId"
                          WHERE ci."AssetId" = h."AssetId" AND c."Status" = 'PENDING')
                      AND NOT EXISTS (
                          SELECT 1 FROM checkout_intent_items ci
                          JOIN checkout_intents c ON c."Id" = ci."CheckoutIntentId"
                          WHERE ci."AssetId" = h."AssetId" AND c."StripeSessionId" IS NOT NULL
                            AND NOT EXISTS (SELECT 1 FROM orders o WHERE o."CheckoutIntentId" = c."Id"))
                      AND NOT EXISTS (
                          SELECT 1 FROM moderation_submissions ms
                          WHERE ms."AssetVersionId" = h."AssetVersionId"
                            AND ms."State" IN ('DRAFT', 'SUBMITTED', 'IN_REVIEW', 'CHANGES_REQUESTED', 'SUPERSEDED'))
                      AND NOT EXISTS (
                          SELECT 1 FROM moderation_submissions ms
                          WHERE ms."AssetVersionId" = h."AssetVersionId" AND ms."State" = 'REJECTED')
                      AND NOT EXISTS (
                          SELECT 1 FROM paid_checkout_reconciliation_holds ph
                          JOIN checkout_intents c ON c."Id" = ph."CheckoutIntentId"
                          JOIN checkout_intent_items ci ON ci."CheckoutIntentId" = c."Id"
                          WHERE ci."AssetId" = h."AssetId" AND ph."State" = 'HELD')
                      AND NOT EXISTS (
                          SELECT 1 FROM code_analysis_runs r
                          WHERE r."AssetVersionId" = h."AssetVersionId" AND r."State" IN ('QUEUED', 'RUNNING'))
                      AND NOT EXISTS (
                          SELECT 1 FROM security_rechecks sr
                          WHERE sr."AssetVersionId" = h."AssetVersionId" AND sr."State" IN ('QUEUED', 'INSPECTING', 'SCANNING'))
                    ORDER BY a."Id", h."FinalizedAt", ra."Id"
                    LIMIT {BATCH_SIZE}
                    FOR UPDATE OF a SKIP LOCKED
                )
                DELETE FROM code_analysis_reuse_artifacts ra
                USING eligible e
                WHERE ra."Id" = e."Id"
                """, cancellationToken).ConfigureAwait(false);

            removed += deleted;
            if (deleted < BATCH_SIZE)
            {
                break;
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Removed or redacted {DetailCount} expired code analysis detail, archive payload, and audit rows", removed);
        }

        return removed;
    }
}
