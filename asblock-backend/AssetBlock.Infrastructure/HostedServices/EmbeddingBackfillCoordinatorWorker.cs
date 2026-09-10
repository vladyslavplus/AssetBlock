using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.HostedServices;

internal sealed class EmbeddingBackfillCoordinatorWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EmbeddingOptions> options,
    IHostEnvironment environment,
    ILogger<EmbeddingBackfillCoordinatorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("IntegrationTesting"))
        {
            logger.LogInformation("EmbeddingBackfillCoordinatorWorker skipped in IntegrationTesting.");
            return;
        }

        EmbeddingOptions opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("EmbeddingBackfillCoordinatorWorker disabled via configuration.");
            return;
        }

        logger.LogInformation("EmbeddingBackfillCoordinatorWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                IEmbeddingBackfillCoordinator coordinator = scope.ServiceProvider.GetRequiredService<IEmbeddingBackfillCoordinator>();
                await coordinator.RunBackfillCycle(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "EmbeddingBackfillCoordinatorWorker cycle failed.");
            }

            try
            {
                var delaySeconds = Math.Clamp(options.Value.BackfillPollSeconds, 1, 3600);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
