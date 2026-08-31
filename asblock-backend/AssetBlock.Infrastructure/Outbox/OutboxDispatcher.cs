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
    Func<double>? jitterProvider = null) : BackgroundService
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(OutboxMessageTypes.LEASE_MINUTES);

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
                await Task.Delay(pollDelay, stoppingToken);
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
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IOutboxStore outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var handlers = scope.ServiceProvider.GetServices<IOutboxMessageHandler>()
            .ToDictionary(h => h.MessageType, StringComparer.Ordinal);

        IReadOnlyList<OutboxMessage> batch = await outbox.ClaimPendingBatch(
            OutboxMessageTypes.DISPATCH_BATCH_SIZE,
            _lease,
            cancellationToken);

        foreach (OutboxMessage message in batch)
        {
            if (message.LockToken is not { } lockToken)
            {
                logger.LogError("Claimed outbox {OutboxId} missing LockToken; skipping mark", message.Id);
                continue;
            }

            var stopwatch = Stopwatch.StartNew();

            if (!handlers.TryGetValue(message.Type, out IOutboxMessageHandler? handler))
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

                continue;
            }

            DiagnosticsOutcome processingOutcome = DiagnosticsOutcome.SUCCESS;
            try
            {
                await handler.Handle(message, cancellationToken);
                if (!await outbox.MarkProcessed(message.Id, lockToken, cancellationToken))
                {
                    logger.LogWarning(
                        "Lost outbox lease for {OutboxId} type {Type} after successful handler; another worker owns it",
                        message.Id,
                        message.Type);
                    processingOutcome = DiagnosticsOutcome.LEASE_LOST;
                }
            }
            catch (OperationCanceledException)
            {
                processingOutcome = DiagnosticsOutcome.CANCELLED;
                throw;
            }
            catch (Exception ex)
            {
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
                    DateTimeOffset next = DateTimeOffset.UtcNow.Add(cappedDelay);
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
                AssetBlockDiagnostics.RecordOutboxProcessing(stopwatch.Elapsed, message.Type, processingOutcome);
            }
        }
    }
}
