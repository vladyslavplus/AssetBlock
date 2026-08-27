using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using AssetBlock.Infrastructure.Observability;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcher> logger) : BackgroundService
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
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task DispatchBatch(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var handlers = scope.ServiceProvider.GetServices<IOutboxMessageHandler>()
            .ToDictionary(h => h.MessageType, StringComparer.Ordinal);

        var batch = await outbox.ClaimPendingBatch(
            OutboxMessageTypes.DISPATCH_BATCH_SIZE,
            _lease,
            cancellationToken);

        foreach (var message in batch)
        {
            if (message.LockToken is not { } lockToken)
            {
                logger.LogError("Claimed outbox {OutboxId} missing LockToken; skipping mark", message.Id);
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            
            if (!handlers.TryGetValue(message.Type, out var handler))
            {
                var outcome = DiagnosticsOutcome.DEAD_LETTER;
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

            var processingOutcome = DiagnosticsOutcome.SUCCESS;
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
                    var delay = TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, Math.Min(message.AttemptCount, 10))));
                    var next = DateTimeOffset.UtcNow.Add(delay);
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
