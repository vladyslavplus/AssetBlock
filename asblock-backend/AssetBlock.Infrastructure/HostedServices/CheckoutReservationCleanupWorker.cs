using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
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
    ILogger<CheckoutReservationCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromMinutes(1);
    /// <summary>Minimum age since create/last poll before another Stripe API check (1–5 min target).</summary>
    private static readonly TimeSpan _reconcileAfter = TimeSpan.FromMinutes(2);
    private const int BATCH_SIZE = 100;

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
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
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
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunCleanup(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var checkoutIntentStore = scope.ServiceProvider.GetRequiredService<ICheckoutIntentStore>();
        var paymentService = scope.ServiceProvider.GetRequiredService<IPaymentService>();
        var completionService = scope.ServiceProvider.GetRequiredService<ICheckoutCompletionService>();
        var now = DateTimeOffset.UtcNow;
        var dueBefore = now - _reconcileAfter;

        var unattached = await checkoutIntentStore.CleanupExpiredUnattachedPendingBatch(
            now,
            BATCH_SIZE,
            cancellationToken);
        if (unattached > 0)
        {
            logger.LogInformation("Cancelled {Count} expired unattached pending checkout intents", unattached);
        }

        // Short TX claim (SKIP LOCKED + lease); Stripe / CompletePaidCheckout stay outside any DB TX.
        var attached = await checkoutIntentStore.ClaimAttachedPendingForStripeSyncBatch(
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
                var session = await paymentService.GetCheckoutSession(stripeSessionId, cancellationToken);
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
