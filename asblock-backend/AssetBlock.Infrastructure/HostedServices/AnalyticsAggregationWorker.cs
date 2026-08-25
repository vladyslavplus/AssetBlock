using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using AssetBlock.Infrastructure.Observability;

namespace AssetBlock.Infrastructure.HostedServices;

/// <summary>
/// Recomputes UTC daily engagement rollups from raw events and performs bounded raw-event retention.
/// </summary>
internal sealed class AnalyticsAggregationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AnalyticsAggregationOptions> options,
    TimeProvider timeProvider,
    IHostEnvironment environment,
    ILogger<AnalyticsAggregationWorker> logger) : BackgroundService
{
    private DateOnly? _lastRetentionDayUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("IntegrationTesting"))
        {
            logger.LogInformation("AnalyticsAggregationWorker skipped in IntegrationTesting.");
            return;
        }

        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("AnalyticsAggregationWorker disabled via configuration.");
            return;
        }

        logger.LogInformation("AnalyticsAggregationWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIteration(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AnalyticsAggregationWorker iteration failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.IntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunIteration(CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var currentDayUtc = DateOnly.FromDateTime(now.UtcDateTime);
        var previousDayUtc = currentDayUtc.AddDays(-1);

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAnalyticsEventStore>();

        var rollupStopwatch = Stopwatch.StartNew();
        var outcome = DiagnosticsOutcome.SUCCESS;
        
        try
        {
            var rollup = await store.TryAcquireAndRecomputeDaily(
                currentDayUtc,
                previousDayUtc,
                now,
                opts.CommandTimeoutSeconds,
                cancellationToken);

            if (rollup.Outcome == AnalyticsDailyRecomputeOutcome.COMPLETED)
            {
                logger.LogInformation(
                    "Analytics daily rollup completed in {DurationMs}ms for days {PreviousDayUtc} and {CurrentDayUtc}; upserted seller={SellerRows} product={ProductRows} collection={CollectionRows} traffic={TrafficRows}",
                    rollupStopwatch.ElapsedMilliseconds,
                    previousDayUtc,
                    currentDayUtc,
                    rollup.SellerRowsUpserted,
                    rollup.ProductRowsUpserted,
                    rollup.CollectionRowsUpserted,
                    rollup.TrafficRowsUpserted);
            }
            else
            {
                outcome = DiagnosticsOutcome.SKIPPED_LOCKED;
                logger.LogDebug(
                    "Analytics daily rollup skipped after {DurationMs}ms; advisory lock held by another worker",
                    rollupStopwatch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = DiagnosticsOutcome.CANCELLED;
            throw;
        }
        catch (Exception)
        {
            outcome = DiagnosticsOutcome.FAILURE;
            throw;
        }
        finally
        {
            AssetBlockDiagnostics.RecordAnalyticsAggregation(rollupStopwatch.Elapsed, outcome);
        }

        if (_lastRetentionDayUtc == currentDayUtc)
        {
            return;
        }

        var cutoffExclusive = now - TimeSpan.FromDays(AnalyticsAggregationConstants.RAW_EVENT_RETENTION_DAYS);
        var retentionStopwatch = Stopwatch.StartNew();
        var retention = await store.TryAcquireAndDeleteExpiredEvents(
            cutoffExclusive,
            opts.RetentionBatchSize,
            opts.MaxRetentionBatchesPerRun,
            opts.CommandTimeoutSeconds,
            cancellationToken);

        if (retention.LockAcquired && !retention.HasBacklog)
        {
            _lastRetentionDayUtc = currentDayUtc;
        }

        if (!retention.LockAcquired)
        {
            logger.LogDebug(
                "Analytics raw event retention skipped after {DurationMs}ms; advisory lock held by another worker",
                retentionStopwatch.ElapsedMilliseconds);
        }
        else if (retention.DeletedCount > 0 || retention.HasBacklog)
        {
            logger.LogInformation(
                "Analytics raw event retention completed in {DurationMs}ms; deleted={DeletedCount} cutoffExclusive={CutoffExclusive}; backlog={HasBacklog}",
                retentionStopwatch.ElapsedMilliseconds,
                retention.DeletedCount,
                cutoffExclusive,
                retention.HasBacklog);
        }
    }
}
