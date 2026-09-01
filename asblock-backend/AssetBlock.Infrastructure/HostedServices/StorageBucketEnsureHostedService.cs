using AssetBlock.Domain.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.HostedServices;

/// <summary>
/// Ensures the configured storage bucket exists, retrying until success or host shutdown.
/// Fast initial attempts cover brief startup races; later attempts use bounded exponential backoff
/// so a late-starting SeaweedFS/MinIO still gets a bucket without requiring an API restart.
/// Does not stop the host on failure; readiness remains unhealthy until EnsureBucket succeeds.
/// </summary>
internal sealed class StorageBucketEnsureHostedService : BackgroundService
{
    private const int FAST_ATTEMPTS = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StorageBucketEnsureHostedService> _logger;
    private readonly TimeSpan _fastRetryDelay;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;

    public StorageBucketEnsureHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<StorageBucketEnsureHostedService> logger)
        : this(
            scopeFactory,
            logger,
            fastRetryDelay: TimeSpan.FromMilliseconds(500),
            initialBackoff: TimeSpan.FromSeconds(2),
            maxBackoff: TimeSpan.FromMinutes(1))
    {
    }

    /// <summary>Test seam for short delays.</summary>
    internal StorageBucketEnsureHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<StorageBucketEnsureHostedService> logger,
        TimeSpan fastRetryDelay,
        TimeSpan initialBackoff,
        TimeSpan maxBackoff)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _fastRetryDelay = fastRetryDelay;
        _initialBackoff = initialBackoff;
        _maxBackoff = maxBackoff;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        TimeSpan backoff = _initialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                IAssetStorageService storage = scope.ServiceProvider.GetRequiredService<IAssetStorageService>();
                await storage.EnsureBucket(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Storage bucket ensure succeeded on attempt {Attempt}.",
                    attempt);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                TimeSpan delay = attempt < FAST_ATTEMPTS ? _fastRetryDelay : backoff;
                _logger.LogWarning(
                    ex,
                    "Storage bucket ensure attempt {Attempt} failed; retrying in {DelayMs}ms. " +
                    "Uploads will fail until the configured bucket exists.",
                    attempt,
                    (int)delay.TotalMilliseconds);

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                if (attempt >= FAST_ATTEMPTS)
                {
                    var nextMs = Math.Min(backoff.TotalMilliseconds * 2, _maxBackoff.TotalMilliseconds);
                    backoff = TimeSpan.FromMilliseconds(nextMs);
                }
            }
        }
    }
}
