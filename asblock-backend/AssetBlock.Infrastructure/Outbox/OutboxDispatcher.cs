using System.Diagnostics;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Common;
using AssetBlock.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcher> logger,
    TimeProvider? timeProvider = null,
    Func<double>? jitterProvider = null,
    TimeSpan? leaseDuration = null,
    int maxConcurrency = 4) : BackgroundService
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _lease = leaseDuration ?? TimeSpan.FromMinutes(OutboxMessageTypes.LEASE_MINUTES);
    private readonly int _maxConcurrency = Math.Clamp(maxConcurrency, 1, OutboxMessageTypes.DISPATCH_BATCH_SIZE);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxDispatcher started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatch(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OutboxDispatcher loop failed");
            }

            try
            {
                TimeSpan pollDelay = CalculatePollInterval(jitterProvider);
                await Task.Delay(pollDelay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal static TimeSpan CalculatePollInterval(Func<double>? jitterProvider = null)
    {
        return DelayJitter.Apply(_pollInterval, jitterProvider);
    }

    internal static TimeSpan CalculateRetryDelay(int attemptCount, Func<double>? jitterProvider = null)
    {
        var baseSeconds = Math.Min(3600, Math.Pow(2, Math.Min(attemptCount, 10)));
        var baseDelay = TimeSpan.FromSeconds(baseSeconds);
        TimeSpan jitteredDelay = DelayJitter.Apply(baseDelay, jitterProvider);
        return jitteredDelay > TimeSpan.FromSeconds(3600) ? TimeSpan.FromSeconds(3600) : jitteredDelay;
    }

    internal async Task DispatchBatch(CancellationToken cancellationToken)
    {
        IReadOnlyList<OutboxMessage> batch;
        await using (AsyncServiceScope claimScope = scopeFactory.CreateAsyncScope())
        {
            IOutboxStore outbox = claimScope.ServiceProvider.GetRequiredService<IOutboxStore>();
            batch = await outbox.ClaimPendingBatch(
                OutboxMessageTypes.DISPATCH_BATCH_SIZE,
                _lease,
                cancellationToken);
        }

        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxConcurrency,
                CancellationToken = cancellationToken
            },
            ProcessMessage);
    }

    private async ValueTask ProcessMessage(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.LockToken is not { } lockToken)
        {
            logger.LogError("Claimed outbox {OutboxId} missing LockToken; skipping mark", message.Id);
            return;
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IOutboxStore outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        IOutboxMessageHandler? handler = scope.ServiceProvider.GetServices<IOutboxMessageHandler>()
            .SingleOrDefault(candidate => string.Equals(candidate.MessageType, message.Type, StringComparison.Ordinal));

        var stopwatch = Stopwatch.StartNew();

        if (handler is null)
        {
            DiagnosticsOutcome outcome = DiagnosticsOutcome.DEAD_LETTER;
            try
            {
                logger.LogError("No handler found for outbox message {OutboxId} of type '{Type}'; moving to dead letter", message.Id, message.Type);
                if (!await outbox.MarkDeadLettered(
                        message.Id,
                        lockToken,
                        $"No handler for outbox type '{message.Type}'.",
                        cancellationToken))
                {
                    logger.LogWarning("Lost outbox lease for {OutboxId} while marking missing-handler dead-letter", message.Id);
                    outcome = DiagnosticsOutcome.LEASE_LOST;
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
                AssetBlockDiagnostics.RecordOutboxProcessing(stopwatch.Elapsed, message.Type, outcome);
            }

            return;
        }

        DiagnosticsOutcome processingOutcome = DiagnosticsOutcome.SUCCESS;
        using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseState = new LeaseRenewalState();
        Task renewalTask = RenewLeaseUntilComplete(message.Id, lockToken, leaseState, handlerCts);
        try
        {
            await handler.Handle(message, handlerCts.Token);
            await handlerCts.CancelAsync();
            await renewalTask;

            if (leaseState.IsLost)
            {
                processingOutcome = DiagnosticsOutcome.LEASE_LOST;
                return;
            }

            if (!await outbox.MarkProcessed(message.Id, lockToken, cancellationToken))
            {
                logger.LogWarning(
                    "Lost outbox lease for {OutboxId} type {Type} after successful handler; another worker owns it",
                    message.Id,
                    message.Type);
                processingOutcome = DiagnosticsOutcome.LEASE_LOST;
            }
        }
        catch (OperationCanceledException) when (leaseState.IsLost && !cancellationToken.IsCancellationRequested)
        {
            processingOutcome = DiagnosticsOutcome.LEASE_LOST;
        }
        catch (OperationCanceledException)
        {
            processingOutcome = DiagnosticsOutcome.CANCELLED;
            throw;
        }
        catch (Exception ex)
        {
            await handlerCts.CancelAsync();
            await renewalTask;
            if (leaseState.IsLost)
            {
                processingOutcome = DiagnosticsOutcome.LEASE_LOST;
                return;
            }

            var maxAttemptsReached = message.AttemptCount >= OutboxMessageTypes.MAX_ATTEMPTS;
            if (maxAttemptsReached)
            {
                processingOutcome = DiagnosticsOutcome.DEAD_LETTER;
                logger.LogError(
                    ex,
                    "Outbox message {OutboxId} of type {Type} reached max attempts ({Attempt}/{Max}); transitioning to dead-letter",
                    message.Id,
                    message.Type,
                    message.AttemptCount,
                    OutboxMessageTypes.MAX_ATTEMPTS);

                if (!await outbox.MarkDeadLettered(message.Id, lockToken, ex.Message, cancellationToken))
                {
                    logger.LogWarning("Lost outbox lease for {OutboxId} while recording dead-letter failure", message.Id);
                    processingOutcome = DiagnosticsOutcome.LEASE_LOST;
                }
            }
            else
            {
                processingOutcome = DiagnosticsOutcome.HANDLER_FAILURE;
                TimeSpan cappedDelay = CalculateRetryDelay(message.AttemptCount, jitterProvider);
                DateTimeOffset next = _timeProvider.GetUtcNow().Add(cappedDelay);
                logger.LogError(
                    ex,
                    "Outbox handler failed for {OutboxId} type {Type} attempt {Attempt}",
                    message.Id,
                    message.Type,
                    message.AttemptCount);
                if (!await outbox.MarkFailed(message.Id, lockToken, ex.Message, next, cancellationToken))
                {
                    logger.LogWarning("Lost outbox lease for {OutboxId} while recording failure", message.Id);
                    processingOutcome = DiagnosticsOutcome.LEASE_LOST;
                }
            }
        }
        finally
        {
            await handlerCts.CancelAsync();
            await renewalTask;
            AssetBlockDiagnostics.RecordOutboxProcessing(stopwatch.Elapsed, message.Type, processingOutcome);
        }
    }

    private async Task RenewLeaseUntilComplete(
        Guid messageId,
        Guid lockToken,
        LeaseRenewalState state,
        CancellationTokenSource handlerCts)
    {
        var interval = TimeSpan.FromTicks(Math.Max(1, _lease.Ticks / 3));
        try
        {
            while (true)
            {
                await Task.Delay(interval, _timeProvider, handlerCts.Token);
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                IOutboxStore renewalStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
                if (!await renewalStore.RenewLease(messageId, lockToken, _lease, handlerCts.Token))
                {
                    logger.LogWarning("Lost outbox lease for {OutboxId} during handler execution", messageId);
                    state.MarkLost();
                    await handlerCts.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (handlerCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not renew outbox lease for {OutboxId}; cancelling handler", messageId);
            state.MarkLost();
            await handlerCts.CancelAsync();
        }
    }

    private sealed class LeaseRenewalState
    {
        private int _lost;

        public bool IsLost => Volatile.Read(ref _lost) != 0;

        public void MarkLost() => Interlocked.Exchange(ref _lost, 1);
    }
}
