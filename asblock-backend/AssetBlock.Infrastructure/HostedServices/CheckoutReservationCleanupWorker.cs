using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.HostedServices;

/// <summary>
/// Reconciles attached checkout sessions with Stripe and cancels unattached expired intents locally.
/// Attached paid sessions are polled on a short backoff (minutes), not only after local expiry.
/// </summary>
internal sealed class CheckoutReservationCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<CheckoutReservationCleanupWorker> logger,
    TimeProvider? timeProvider = null,
    Func<double>? jitterProvider = null) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromMinutes(1);
    /// <summary>Minimum age since create/last poll before another Stripe API check (1–5 min target).</summary>
    private static readonly TimeSpan _reconcileAfter = TimeSpan.FromMinutes(2);
    private const int BATCH_SIZE = 100;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("IntegrationTesting"))
        {
            logger.LogInformation("CheckoutReservationCleanupWorker skipped in IntegrationTesting.");
            return;
        }

        logger.LogInformation("CheckoutReservationCleanupWorker started");

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
                logger.LogError(ex, "CheckoutReservationCleanupWorker cycle failed");
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
        return DelayJitter.Apply(TimeSpan.FromSeconds(30), jitterProvider);
    }

    internal static TimeSpan CalculateIntervalDelay(Func<double>? jitterProvider = null)
    {
        return DelayJitter.Apply(_interval, jitterProvider);
    }

    internal async Task RunCleanup(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ICheckoutIntentStore checkoutIntentStore = scope.ServiceProvider.GetRequiredService<ICheckoutIntentStore>();
        IPaymentService paymentService = scope.ServiceProvider.GetRequiredService<IPaymentService>();
        ICheckoutCompletionService completionService = scope.ServiceProvider.GetRequiredService<ICheckoutCompletionService>();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset dueBefore = now - _reconcileAfter;

        var unattached = await checkoutIntentStore.CleanupExpiredUnattachedPendingBatch(
            now,
            BATCH_SIZE,
            cancellationToken);
        if (unattached > 0)
        {
            logger.LogInformation("Cancelled {Count} expired unattached pending checkout intents", unattached);
        }

        // Short TX claim (SKIP LOCKED + lease); Stripe / CompletePaidCheckout stay outside any DB TX.
        IReadOnlyList<(Guid Id, string StripeSessionId)> attached = await checkoutIntentStore.ClaimAttachedPendingForStripeSyncBatch(
            now,
            dueBefore,
            BATCH_SIZE,
            cancellationToken);
        var cancelledAttached = 0;
        var reconciledCompleted = 0;
        foreach ((Guid id, var stripeSessionId) in attached)
        {
            try
            {
                StripeCheckoutSessionSnapshot session = await paymentService.GetCheckoutSession(stripeSessionId, cancellationToken);
                if (string.Equals(
                        session.Status,
                        StripeConstants.CheckoutSessionStatuses.COMPLETE,
                        StringComparison.OrdinalIgnoreCase)
                    && session.CompletedCheckout is not null)
                {
                    await completionService.CompletePaidCheckout(
                        session.CompletedCheckout,
                        cancellationToken);
                    reconciledCompleted++;
                    continue;
                }

                if (string.Equals(
                        session.Status,
                        StripeConstants.CheckoutSessionStatuses.EXPIRED,
                        StringComparison.OrdinalIgnoreCase)
                    && await checkoutIntentStore.TryCancelAndRelease(id, cancellationToken))
                {
                    cancelledAttached++;
                }

                // OPEN (or other non-terminal): reservation kept; claim already leased via LastStripeReconciledAt.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to reconcile attached checkout intent {CheckoutIntentId} with Stripe",
                    id);
            }
        }

        if (cancelledAttached > 0)
        {
            logger.LogInformation(
                "Cancelled {Count} checkout intents after Stripe reported expired",
                cancelledAttached);
        }

        if (reconciledCompleted > 0)
        {
            logger.LogInformation(
                "Reconciled {Count} completed Stripe checkout sessions without relying on webhook redelivery",
                reconciledCompleted);
        }
    }
}

/// <summary>
/// Periodically removes expired refresh tokens in bounded batches.
/// Preserves unexpired revoked tokens for the 15-second grace window and reuse detection.
/// </summary>
internal sealed class RefreshTokenRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<RefreshTokenRetentionWorker> logger,
    TimeProvider? timeProvider = null,
    Func<double>? jitterProvider = null) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private const int BATCH_SIZE = 500;
    private const int MAX_BATCHES_PER_CYCLE = 20;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("IntegrationTesting"))
        {
            logger.LogInformation("RefreshTokenRetentionWorker skipped in IntegrationTesting.");
            return;
        }

        logger.LogInformation("RefreshTokenRetentionWorker started");

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
                logger.LogError(ex, "RefreshTokenRetentionWorker cycle failed");
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
        IJwtTokenService jwtTokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var totalDeleted = 0;

        for (var i = 0; i < MAX_BATCHES_PER_CYCLE; i++)
        {
            var deleted = await jwtTokenService.CleanupExpiredTokens(now, BATCH_SIZE, cancellationToken);
            totalDeleted += deleted;
            if (deleted < BATCH_SIZE)
            {
                break;
            }
        }

        if (totalDeleted > 0)
        {
            logger.LogInformation("Cleaned up {TotalDeleted} expired refresh tokens in retention cycle", totalDeleted);
        }

        return totalDeleted;
    }
}
