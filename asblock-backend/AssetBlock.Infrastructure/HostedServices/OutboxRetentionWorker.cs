using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.HostedServices;

/// <summary>
/// Periodically removes processed outbox messages older than the retention cutoff (7 days) in bounded batches.
/// Only PROCESSED messages with non-null ProcessedAt are deleted; pending, leased, retryable, and DEAD_LETTERED rows are preserved.
/// </summary>
internal sealed class OutboxRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<OutboxRetentionWorker> logger,
    TimeProvider? timeProvider = null,
    Func<double>? jitterProvider = null) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan _retentionPeriod = TimeSpan.FromDays(7);
    private const int BATCH_SIZE = 500;
    private const int MAX_BATCHES_PER_CYCLE = 20;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("IntegrationTesting"))
        {
            logger.LogInformation("OutboxRetentionWorker skipped in IntegrationTesting.");
            return;
        }

        logger.LogInformation("OutboxRetentionWorker started");

        try
        {
            TimeSpan initialDelay = CalculateInitialDelay(jitterProvider);
            await Task.Delay(initialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
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
                logger.LogError(ex, "OutboxRetentionWorker cycle failed");
            }

            try
            {
                TimeSpan loopDelay = CalculateIntervalDelay(jitterProvider);
                await Task.Delay(loopDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal static TimeSpan CalculateInitialDelay(Func<double>? jitterProvider = null)
    {
        return DelayJitter.Apply(TimeSpan.FromMinutes(2), jitterProvider);
    }

    internal static TimeSpan CalculateIntervalDelay(Func<double>? jitterProvider = null)
    {
        return DelayJitter.Apply(_interval, jitterProvider);
    }

    internal async Task<int> RunCleanup(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IOutboxStore outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - _retentionPeriod;
        var totalDeleted = 0;

        for (var i = 0; i < MAX_BATCHES_PER_CYCLE; i++)
        {
            var deleted = await outboxStore.CleanupProcessed(cutoff, BATCH_SIZE, cancellationToken);
            totalDeleted += deleted;
            if (deleted < BATCH_SIZE)
            {
                break;
            }
        }

        if (totalDeleted > 0)
        {
            logger.LogInformation("Cleaned up {TotalDeleted} processed outbox messages in retention cycle", totalDeleted);
        }

        return totalDeleted;
    }
}
