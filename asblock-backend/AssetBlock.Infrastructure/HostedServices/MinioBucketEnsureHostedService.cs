using AssetBlock.Domain.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.HostedServices;

/// <summary>
/// Ensures the MinIO bucket exists on application startup so uploads work without manual bucket creation.
/// </summary>
internal sealed class MinioBucketEnsureHostedService(IServiceProvider services, ILogger<MinioBucketEnsureHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IAssetStorageService>();
            await storage.EnsureBucketAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MinIO bucket ensure failed at startup; uploads may still work if bucket already exists.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
